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

/// <summary>An audit key provider that can resolve a key by its id — needed to verify an audit that
/// was signed under a previous key after the signing key has been rotated.</summary>
public interface IRotatableAuditKeyProvider : IAuditKeyProvider
{
    bool TryGetKey(string keyId, out byte[] key);
}

/// <summary>
/// Holds a current signing key plus any prior keys, each addressed by its <c>KeyId</c>. Signs with
/// the current key; verifies a stored audit with the key its recorded <c>KeyId</c> names — so
/// rotating the signing key never invalidates already-signed audits. Fail-closed: an unknown KeyId
/// does not verify, and a key shorter than 128-bit is rejected at construction.
/// </summary>
public sealed class RotatingAuditKeyProvider : IRotatableAuditKeyProvider
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    public string KeyId { get; }

    public RotatingAuditKeyProvider(string currentKeyId, byte[] currentKey, IReadOnlyDictionary<string, byte[]>? priorKeys = null)
    {
        if (string.IsNullOrWhiteSpace(currentKeyId)) throw new ArgumentException("currentKeyId is required.", nameof(currentKeyId));
        if (currentKey.Length < 16) throw new InvalidOperationException("Audit signing key must be at least 16 bytes (128-bit).");
        if (priorKeys is not null) foreach (var kv in priorKeys) _keys[kv.Key] = kv.Value;
        _keys[currentKeyId] = currentKey;
        KeyId = currentKeyId;
    }

    public byte[] GetKey() => _keys[KeyId];
    public bool TryGetKey(string keyId, out byte[] key) => _keys.TryGetValue(keyId, out key!);
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

    /// <summary>Verify a PERSISTED audit using the key its own <c>KeyId</c> names (rotation-aware):
    /// the signature is checked against the audit's recorded chain head. A rotatable provider resolves
    /// the historical key by id; otherwise the provider's single key is used only if its id matches.
    /// An unknown KeyId fails closed.</summary>
    public static bool Verify(SignedAudit signed, IAuditKeyProvider provider)
    {
        var key = ResolveKey(signed.KeyId, provider);
        if (key is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hmac(signed.ChainHash, key)),
            Encoding.UTF8.GetBytes(signed.Signature ?? ""));
    }

    /// <summary>Resolve the key a stored artifact was signed under, by its recorded <paramref name="keyId"/>:
    /// a rotatable provider looks up the historical key; a single-key provider answers only if its id
    /// matches. Returns null (→ caller fails closed) for an unknown id — so rotating the current key
    /// never silently verifies an old artifact with the wrong key. The one resolution rule shared by
    /// audit-level and bundle-level verification.</summary>
    public static byte[]? ResolveKey(string keyId, IAuditKeyProvider provider) =>
        provider is IRotatableAuditKeyProvider rot && rot.TryGetKey(keyId, out var k) ? k
        : string.Equals(keyId, provider.KeyId, StringComparison.Ordinal) ? provider.GetKey()
        : null;

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

/// <summary>An audit key provider pinned to one explicit (keyId, key) pair. Used to RE-sign a run
/// under the exact key id a saved bundle recorded — so a deterministic replay reproduces the original
/// signature byte-for-byte even after the current signing key has rotated.</summary>
public sealed class FixedKeyProvider : IAuditKeyProvider
{
    private readonly byte[] _key;
    public FixedKeyProvider(string keyId, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(keyId)) throw new ArgumentException("keyId is required.", nameof(keyId));
        KeyId = keyId; _key = key;
    }
    public byte[] GetKey() => _key;
    public string KeyId { get; }
}

/// <summary>
/// Builds the audit key provider a host/CLI uses, with rotation support sourced from the environment.
/// The CURRENT key comes from <see cref="EnvironmentAuditKeyProvider"/>; PRIOR keys (so already-signed
/// runs still verify after rotation) come from <c>INTENTMESH_AUDIT_PRIOR_KEYS</c>, a
/// <c>id=base64;id2=base64</c> list. With no priors configured it returns the single-key provider —
/// which still resolves strictly by recorded key id (fail-closed), it just can't verify a run signed
/// under a key it was never told about.
/// </summary>
public static class AuditKeyProviders
{
    public const string PriorKeysEnv = "INTENTMESH_AUDIT_PRIOR_KEYS";

    public static IAuditKeyProvider FromEnvironment()
    {
        var current = new EnvironmentAuditKeyProvider();
        var priors = ParsePriors(Environment.GetEnvironmentVariable(PriorKeysEnv));
        return priors.Count == 0
            ? current
            : new RotatingAuditKeyProvider(current.KeyId, current.GetKey(), priors);
    }

    private static Dictionary<string, byte[]> ParsePriors(string? raw)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var id = pair[..eq].Trim();
            try { map[id] = Convert.FromBase64String(pair[(eq + 1)..].Trim()); }
            catch (FormatException) { /* skip a malformed prior-key entry rather than fail startup */ }
        }
        return map;
    }
}
