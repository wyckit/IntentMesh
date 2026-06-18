using System.Security.Cryptography;
using System.Text;

namespace IntentMesh.Core;

/// <summary>A tamper-evident audit envelope: the canonical audit JSON, a hash chain over the audit
/// events, an HMAC signature over the chain head, and the id of the key that signed it (so a
/// verifier knows which key — and can refuse demo-signed audits in production).</summary>
public sealed record SignedAudit(string AuditJson, string ChainHash, string Signature, string KeyId = AuditSigner.DemoKeyId);

/// <summary>The source of the HMAC signing key. Injectable so the raw key need not live in the
/// binary — back it with a KMS/HSM/secret store in production; the kernel only sees bytes + an id.
/// This is the "design a seam, not a secret" convention.</summary>
public interface IAuditKeyProvider
{
    byte[] GetKey();
    string KeyId { get; }
}

/// <summary>
/// Resolves the audit signing key from the environment: <c>INTENTMESH_AUDIT_KEY</c> (base64 or raw
/// utf8, ≥16 bytes) with an optional <c>INTENTMESH_AUDIT_KEY_ID</c>. When unset it falls back to a
/// clearly-labelled INSECURE demo key so demos run, but <see cref="IsProductionKey"/> is false —
/// a production host should assert it is true at startup. A configured-but-too-short key is rejected
/// (fail-closed) rather than silently accepted.
/// </summary>
public sealed class EnvironmentAuditKeyProvider : IAuditKeyProvider
{
    public const string KeyEnv = "INTENTMESH_AUDIT_KEY";
    public const string KeyIdEnv = "INTENTMESH_AUDIT_KEY_ID";

    private readonly byte[] _key;
    public string KeyId { get; }
    public bool IsProductionKey { get; }

    public EnvironmentAuditKeyProvider()
    {
        var raw = Environment.GetEnvironmentVariable(KeyEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            _key = AuditSigner.DemoKeyBytes;
            KeyId = AuditSigner.DemoKeyId;
            IsProductionKey = false;
            return;
        }
        _key = Decode(raw);
        if (_key.Length < 16)
            throw new InvalidOperationException($"{KeyEnv} must be at least 16 bytes (128-bit); got {_key.Length}.");
        IsProductionKey = true;
        KeyId = Environment.GetEnvironmentVariable(KeyIdEnv) ?? "env-" + ShortHash(_key);
    }

    public byte[] GetKey() => _key;

    private static byte[] Decode(string raw)
    {
        try { return Convert.FromBase64String(raw.Trim()); }
        catch (FormatException) { return Encoding.UTF8.GetBytes(raw); }
    }

    internal static string ShortHash(byte[] key)
        => Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..8];
}

/// <summary>
/// Signs an audit trail. Each audit event is folded into a SHA-256 hash chain
/// (h_i = SHA256(h_(i-1) || event_i)); the chain head is then HMAC-signed with a key from an
/// <see cref="IAuditKeyProvider"/>. Deterministic given the run and key, so two runs of the same
/// prompt produce identical signatures — and changing, reordering, or dropping any event changes
/// the chain head, so tampering is detectable by Verify. The signing key is sourced from the
/// environment by default (see <see cref="EnvironmentAuditKeyProvider"/>), never hardcoded for
/// production; the demo key is used only as a clearly-labelled fallback.
/// </summary>
public static class AuditSigner
{
    /// <summary>Clearly-labelled NON-secret demo key id — production audits use an env/KMS key id.</summary>
    public const string DemoKeyId = "demo-v1-INSECURE";

    // Fixed demo key — NOT a production secret; used only when no key is configured.
    internal static readonly byte[] DemoKeyBytes = Encoding.UTF8.GetBytes("intentmesh-demo-audit-key-v1");

    /// <summary>The process-wide default key provider (reads the environment once).</summary>
    public static IAuditKeyProvider Default { get; } = new EnvironmentAuditKeyProvider();

    public static SignedAudit Sign(RunResult result, byte[]? key = null) => SignWith(result, Resolve(key));
    public static SignedAudit Sign(RunResult result, IAuditKeyProvider provider) => SignWith(result, provider);

    private static SignedAudit SignWith(RunResult result, IAuditKeyProvider provider)
    {
        var chain = ChainHash(result);
        return new SignedAudit(AuditExporter.ToJson(result), chain, Hmac(chain, provider.GetKey()), provider.KeyId);
    }

    /// <summary>Deterministic HMAC-SHA256 (hex) over arbitrary canonical text — used to sign a
    /// trace bundle over its five artifacts' canonical JSON.</summary>
    public static string SignString(string canonical, byte[]? key = null) => Hmac(canonical, (key ?? Default.GetKey()));
    public static string SignString(string canonical, IAuditKeyProvider provider) => Hmac(canonical, provider.GetKey());

    /// <summary>True iff the signature matches a fresh chain hash of the result under the given key
    /// (no tampering, right key). A demo-signed audit will NOT verify under a production key.</summary>
    public static bool Verify(RunResult result, string signature, byte[]? key = null) => VerifyWith(result, signature, Resolve(key));
    public static bool Verify(RunResult result, string signature, IAuditKeyProvider provider) => VerifyWith(result, signature, provider);

    private static bool VerifyWith(RunResult result, string signature, IAuditKeyProvider provider)
    {
        var expected = Hmac(ChainHash(result), provider.GetKey());
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature ?? ""));
    }

    private static IAuditKeyProvider Resolve(byte[]? key) => key is null ? Default : new RawKeyProvider(key);

    private static string Hmac(string canonical, byte[] key)
        => Convert.ToHexString(new HMACSHA256(key).ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static string ChainHash(RunResult result)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("intentmesh-audit-v2"));
        foreach (var e in result.Audit)
        {
            // Length-prefixed, unambiguous encoding: each field is written as <utf8-byte-length>:<bytes>
            // so no two distinct (Seq, Phase, NodeId, Message) tuples can collide by shifting
            // characters across field boundaries (a plain concatenation could).
            var sb = new StringBuilder();
            AppendField(sb, e.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendField(sb, e.Phase);
            AppendField(sb, e.NodeId);
            AppendField(sb, e.Message);
            h = SHA256.HashData(Concat(h, Encoding.UTF8.GetBytes(sb.ToString())));
        }
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder sb, string? value)
    {
        var s = value ?? "";
        sb.Append(Encoding.UTF8.GetByteCount(s)).Append(':').Append(s).Append('|');
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    /// <summary>Wraps an explicitly-supplied raw key (e.g. a test key) with a derived id.</summary>
    private sealed class RawKeyProvider : IAuditKeyProvider
    {
        private readonly byte[] _key;
        public RawKeyProvider(byte[] key) { _key = key; KeyId = "raw-" + EnvironmentAuditKeyProvider.ShortHash(key); }
        public byte[] GetKey() => _key;
        public string KeyId { get; }
    }
}
