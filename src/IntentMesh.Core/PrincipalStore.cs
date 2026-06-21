using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntentMesh.Core;

/// <summary>One provisioned principal: a stable id, its tenant, its roles, and the SHA-256 hash (hex)
/// of its API key. The plaintext API key is never stored — provisioning computes the hash via
/// <see cref="PrincipalStore.HashApiKey"/>.</summary>
public sealed record PrincipalRecord(string Id, string Tenant, string[] Roles, string ApiKeyHash);

/// <summary>
/// A file/inline-JSON backed principal directory. Exchanges a presented API key for an
/// <see cref="AuthPrincipal"/> via a constant-time hash comparison. This is the zero-dependency
/// identity source for the built-in token mode; an external IdP can be used instead via trusted-proxy
/// headers (see <see cref="TrustedProxyAuth"/>).
/// </summary>
public sealed class PrincipalStore
{
    public const string Env = "INTENTMESH_PRINCIPALS";   // a JSON file path, or inline JSON
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly List<PrincipalRecord> _records;
    public int Count => _records.Count;

    public PrincipalStore(IEnumerable<PrincipalRecord> records) => _records = records.ToList();

    /// <summary>SHA-256 (lowercase hex) of an API key — the value stored in <see cref="PrincipalRecord.ApiKeyHash"/>.</summary>
    public static string HashApiKey(string apiKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();

    /// <summary>Authenticate an API key. Compares the presented key's hash against every record in
    /// constant time (no early-out on the first mismatch), returning the matched principal.</summary>
    public bool TryAuthenticate(string? apiKey, out AuthPrincipal principal)
    {
        principal = null!;
        if (string.IsNullOrEmpty(apiKey)) return false;
        var presented = Encoding.UTF8.GetBytes(HashApiKey(apiKey));
        PrincipalRecord? match = null;
        foreach (var r in _records)
        {
            var stored = Encoding.UTF8.GetBytes(r.ApiKeyHash ?? "");
            // FixedTimeEquals is constant-time only for equal lengths; gate on length first (the hash is a
            // fixed 64-hex-char string, so a length mismatch only means a malformed record).
            if (stored.Length == presented.Length && CryptographicOperations.FixedTimeEquals(stored, presented))
                match = r;
        }
        if (match is null || !AuthIds.IsValid(match.Id) || !AuthIds.IsValid(match.Tenant)) return false;
        principal = new AuthPrincipal(match.Id, match.Tenant, Roles.Set(match.Roles ?? Array.Empty<string>()));
        return true;
    }

    public static PrincipalStore FromJson(string json)
    {
        var records = JsonSerializer.Deserialize<PrincipalRecord[]>(json, Json) ?? Array.Empty<PrincipalRecord>();
        return new PrincipalStore(records);
    }

    /// <summary>Load from <c>INTENTMESH_PRINCIPALS</c>: a path to a JSON file if it exists, else the
    /// value treated as inline JSON. Unset → an empty store (no built-in token identities configured).</summary>
    public static PrincipalStore FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(Env);
        if (string.IsNullOrWhiteSpace(raw)) return new PrincipalStore(Array.Empty<PrincipalRecord>());
        var json = File.Exists(raw) ? File.ReadAllText(raw) : raw;
        return FromJson(json);
    }
}
