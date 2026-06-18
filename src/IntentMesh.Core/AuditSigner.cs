using System.Security.Cryptography;
using System.Text;

namespace IntentMesh.Core;

/// <summary>A tamper-evident audit envelope: the canonical audit JSON, a hash chain over the audit
/// events, and an HMAC signature over the chain head.</summary>
public sealed record SignedAudit(string AuditJson, string ChainHash, string Signature);

/// <summary>
/// Signs an audit trail (v1.0). Each audit event is folded into a SHA-256 hash chain
/// (h_i = SHA256(h_(i-1) || event_i)); the chain head is then HMAC-signed. Deterministic given the
/// run and key, so two runs of the same prompt produce identical signatures — and changing,
/// reordering, or dropping any event changes the chain head, so tampering is detectable by Verify.
///
/// The key here is a fixed demo key; production key management (rotation, HSM/KMS) is out of scope.
/// </summary>
public static class AuditSigner
{
    // Fixed demo key — NOT a production secret. v1.0 production would source this from a KMS/HSM.
    private static readonly byte[] DemoKey = Encoding.UTF8.GetBytes("intentmesh-demo-audit-key-v1");

    public static SignedAudit Sign(RunResult result, byte[]? key = null)
    {
        var chain = ChainHash(result);
        return new SignedAudit(AuditExporter.ToJson(result), chain, SignString(chain, key));
    }

    /// <summary>Deterministic HMAC-SHA256 (hex) over arbitrary canonical text — used to sign a
    /// trace bundle over its five artifacts' canonical JSON.</summary>
    public static string SignString(string canonical, byte[]? key = null)
        => Convert.ToHexString(new HMACSHA256(key ?? DemoKey).ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    /// <summary>True iff the signature matches a fresh chain hash of the result (no tampering).</summary>
    public static bool Verify(RunResult result, string signature, byte[]? key = null)
    {
        var expected = Convert.ToHexString(new HMACSHA256(key ?? DemoKey).ComputeHash(Encoding.UTF8.GetBytes(ChainHash(result)))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature ?? ""));
    }

    private static string ChainHash(RunResult result)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("intentmesh-audit-v1"));
        foreach (var e in result.Audit)
        {
            var record = $"{e.Seq}{e.Phase}{e.NodeId}{e.Message}";
            h = SHA256.HashData(Concat(h, Encoding.UTF8.GetBytes(record)));
        }
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
