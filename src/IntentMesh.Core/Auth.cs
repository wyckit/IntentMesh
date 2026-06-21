using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntentMesh.Core;

/// <summary>
/// Multi-tenant authorization primitives for the Control Room. A request is bound to an
/// <see cref="AuthPrincipal"/> (who, which tenant, which roles); runs are partitioned by tenant; and
/// an approval is only ever applied when the caller presents a <b>server-issued</b>
/// <see cref="ApprovalChallenge"/> for that exact run+node+tenant — never a caller-asserted node id.
/// All signing is HMAC-SHA256 (zero external dependencies), reusing the 128-bit key floor.
/// </summary>
public static class Roles
{
    public const string Viewer = "viewer";       // read runs in your tenant
    public const string Operator = "operator";   // run the pipeline (propose -> gate -> execute)
    public const string Approver = "approver";   // approve gated (Confirm) nodes via server-issued challenge
    public const string Admin = "admin";         // superset of all roles within the tenant

    /// <summary>Build a case-insensitive role set, dropping blanks.</summary>
    public static IReadOnlySet<string> Set(IEnumerable<string> roles)
        => new HashSet<string>(roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>An authenticated caller: a principal id, the tenant it belongs to, and its roles. The
/// <c>admin</c> role is a superset — <see cref="Has"/> returns true for any role when admin is held.</summary>
public sealed record AuthPrincipal(string PrincipalId, string TenantId, IReadOnlySet<string> Roles)
{
    public bool Has(string role) => Roles.Contains(role) || Roles.Contains(Core.Roles.Admin);
}

/// <summary>Validation for principal/tenant ids. They become a path segment (per-tenant run root), so
/// they are restricted to a safe, traversal-free shape — a tenant id can never escape the runs root.
/// Beyond the alphabet, an id must contain a letter or digit and may not start with '.' or '-', which
/// rejects "." / ".." / dotfiles / option-like names (e.g. "-rf") even though their characters are in
/// the allowed set.</summary>
public static class AuthIds
{
    public static bool IsValid(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 64
           && id[0] != '.' && id[0] != '-'
           && id.Any(char.IsAsciiLetterOrDigit)
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

/// <summary>Key material parsing shared by auth-key consumers: base64 first, else raw UTF-8 bytes,
/// enforcing the same 128-bit floor as the audit key.</summary>
public static class AuthKeys
{
    public static byte[] Parse(string raw)
    {
        byte[] key;
        try { key = Convert.FromBase64String(raw.Trim()); }
        catch (FormatException) { key = Encoding.UTF8.GetBytes(raw); }
        return AuditSigner.RequireStrongKey(key, "auth key (INTENTMESH_AUTH_KEY)");
    }
}

/// <summary>
/// Compact HMAC-signed token codec: <c>im1.&lt;base64url(payload)&gt;.&lt;base64url(hmac)&gt;</c>.
/// Verification HMACs the received payload bytes verbatim (never re-serialized), so it is robust to any
/// JSON formatting and uses a constant-time comparison.
/// </summary>
public static class SignedToken
{
    private const string Prefix = "im1";

    public static string Encode(string payloadJson, byte[] key)
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        var sig = HMACSHA256.HashData(key, payload);
        return $"{Prefix}.{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(sig)}";
    }

    public static bool TryDecode(string? token, byte[] key, out string payloadJson)
    {
        payloadJson = "";
        if (string.IsNullOrEmpty(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix) return false;
        byte[] payload, sig;
        try { payload = Base64Url.DecodeFromChars(parts[1]); sig = Base64Url.DecodeFromChars(parts[2]); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(key, payload);
        if (!CryptographicOperations.FixedTimeEquals(sig, expected)) return false;
        payloadJson = Encoding.UTF8.GetString(payload);
        return true;
    }
}

/// <summary>Mints and verifies session tokens — the bearer credential a caller presents after
/// exchanging an API key. The <c>typ</c> field is checked on verify so a token of another kind
/// (e.g. an approval challenge) can never be replayed as a session.</summary>
public sealed class AuthTokenService
{
    private sealed record Payload(string typ, string sub, string ten, string[] rol, long iat, long exp, string jti);
    private static readonly JsonSerializerOptions Json = new() { };

    private readonly byte[] _key;
    public AuthTokenService(byte[] key) => _key = AuditSigner.RequireStrongKey(key, "auth token key");

    public string Mint(AuthPrincipal principal, long issuedAtUnix, long expiresAtUnix, string nonce)
    {
        var p = new Payload("sess", principal.PrincipalId, principal.TenantId,
            principal.Roles.ToArray(), issuedAtUnix, expiresAtUnix, nonce);
        return SignedToken.Encode(JsonSerializer.Serialize(p, Json), _key);
    }

    public bool TryVerify(string? token, long nowUnix, out AuthPrincipal principal)
    {
        principal = null!;
        if (!SignedToken.TryDecode(token, _key, out var json)) return false;
        Payload? p;
        try { p = JsonSerializer.Deserialize<Payload>(json, Json); }
        catch (JsonException) { return false; }
        if (p is null || p.typ != "sess" || p.exp <= nowUnix) return false;
        if (!AuthIds.IsValid(p.sub) || !AuthIds.IsValid(p.ten)) return false;
        principal = new AuthPrincipal(p.sub, p.ten, Roles.Set(p.rol ?? Array.Empty<string>()));
        return true;
    }
}

/// <summary>
/// Server-issued approval challenges. When a run halts with a gated (Confirm) node, the server mints a
/// signed challenge bound to <c>{runId, nodeId, tenantId, exp}</c>. The only way to apply an approval is
/// to present that challenge back: a caller can no longer self-assert a node id, and a challenge for one
/// run/tenant cannot approve a node in another.
/// </summary>
public sealed class ApprovalChallengeService
{
    private sealed record Payload(string typ, string run, string node, string ten, long iat, long exp, string jti);
    private static readonly JsonSerializerOptions Json = new() { };

    private readonly byte[] _key;
    public ApprovalChallengeService(byte[] key) => _key = AuditSigner.RequireStrongKey(key, "approval challenge key");

    public string Mint(string runId, string nodeId, string tenantId, long issuedAtUnix, long expiresAtUnix, string nonce)
    {
        var p = new Payload("appr", runId, nodeId, tenantId, issuedAtUnix, expiresAtUnix, nonce);
        return SignedToken.Encode(JsonSerializer.Serialize(p, Json), _key);
    }

    /// <summary>Verify a presented challenge against the run+tenant the caller is acting on. Returns the
    /// attested node id only when the signature, type, run, tenant, and expiry all hold.</summary>
    public bool TryVerify(string? token, string expectedRunId, string expectedTenant, long nowUnix, out string nodeId)
    {
        nodeId = "";
        if (!SignedToken.TryDecode(token, _key, out var json)) return false;
        Payload? p;
        try { p = JsonSerializer.Deserialize<Payload>(json, Json); }
        catch (JsonException) { return false; }
        if (p is null || p.typ != "appr" || p.exp <= nowUnix) return false;
        if (!string.Equals(p.run, expectedRunId, StringComparison.Ordinal)) return false;
        if (!string.Equals(p.ten, expectedTenant, StringComparison.Ordinal)) return false;
        nodeId = p.node;
        return true;
    }
}

/// <summary>Maps verified reverse-proxy / OIDC headers to a principal. The TRUST decision (is this
/// request actually from the configured proxy hop?) is made by the host before calling this; here we
/// only validate and shape the asserted identity.</summary>
public static class TrustedProxyAuth
{
    public static bool TryFromHeaders(string? principal, string? tenant, string? roles, out AuthPrincipal p)
    {
        p = null!;
        if (!AuthIds.IsValid(principal) || !AuthIds.IsValid(tenant)) return false;
        var roleArr = (roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        p = new AuthPrincipal(principal!, tenant!, Roles.Set(roleArr));
        return true;
    }
}
