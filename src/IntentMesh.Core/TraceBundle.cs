using System.Text.Json;

namespace IntentMesh.Core;

// The five versioned run artifacts (the "show your work" bundle). Each wraps the relevant slice of
// a RunResult with a schema version, so it is independently inspectable, diffable, and signable.

public sealed record IntentGraphArtifact(string SchemaVersion, string Prompt, IReadOnlyList<NodeView> Nodes);
public sealed record PolicyDecisionsArtifact(string SchemaVersion, IReadOnlyList<PolicyView> Decisions);
public sealed record ExecutionTraceArtifact(string SchemaVersion, IReadOnlyList<ExecView> Executions);
public sealed record VerificationReportArtifact(string SchemaVersion, bool AllPass, IReadOnlyList<VerifyView> Results);
public sealed record SignedAuditArtifact(string SchemaVersion, IReadOnlyList<AuditView> Events, string ChainHash, string Signature, string KeyId = AuditSigner.DemoKeyId);

/// <summary>
/// A complete, signed trace bundle for one run: the prompt, the approvals that produced it, the
/// summary, the five artifacts, and an HMAC over all five (tamper-evident). Deterministic: the same
/// prompt + approvals yields a byte-identical bundle and signature — the basis for `replay`.
/// </summary>
public sealed record TraceBundle(
    string SchemaVersion,
    string Prompt,
    IReadOnlyList<string> Approvals,
    SummaryView Summary,
    IntentGraphArtifact IntentGraph,
    PolicyDecisionsArtifact PolicyDecisions,
    ExecutionTraceArtifact ExecutionTrace,
    VerificationReportArtifact VerificationReport,
    SignedAuditArtifact SignedAudit,
    string BundleSignature);

public static class TraceBundleBuilder
{
    public const string SchemaVersion = "1.0";

    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static TraceBundle From(RunResult r, IReadOnlyList<string>? approvals = null, byte[]? key = null)
    {
        var appr = (approvals ?? Array.Empty<string>()).OrderBy(a => a, StringComparer.Ordinal).ToList();
        var ig = new IntentGraphArtifact(SchemaVersion, r.Prompt, r.Nodes);
        var pd = new PolicyDecisionsArtifact(SchemaVersion, r.Policy);
        var et = new ExecutionTraceArtifact(SchemaVersion, r.Execution);
        var vr = new VerificationReportArtifact(SchemaVersion, r.Verification.All(v => v.Pass), r.Verification);
        var signed = AuditSigner.Sign(r, key);
        var sa = new SignedAuditArtifact(SchemaVersion, r.Audit, signed.ChainHash, signed.Signature, signed.KeyId);

        // The bundle signature covers the canonical JSON of all five artifacts.
        var canonical = string.Join("\n",
            JsonSerializer.Serialize(ig, Canonical),
            JsonSerializer.Serialize(pd, Canonical),
            JsonSerializer.Serialize(et, Canonical),
            JsonSerializer.Serialize(vr, Canonical),
            JsonSerializer.Serialize(sa, Canonical));
        var bundleSig = AuditSigner.SignString(canonical, key);

        return new TraceBundle(SchemaVersion, r.Prompt, appr, r.Summary, ig, pd, et, vr, sa, bundleSig);
    }

    public static string ToJson(TraceBundle b) => JsonSerializer.Serialize(b, Pretty);
    public static TraceBundle FromJson(string json) =>
        JsonSerializer.Deserialize<TraceBundle>(json, Pretty)
        ?? throw new InvalidDataException("Not a valid IntentMesh trace bundle.");

    /// <summary>The five artifacts as separately-named JSON documents (for split export).</summary>
    public static IReadOnlyDictionary<string, string> SplitFiles(TraceBundle b) => new Dictionary<string, string>
    {
        ["intent.graph.json"] = JsonSerializer.Serialize(b.IntentGraph, Pretty),
        ["policy.decisions.json"] = JsonSerializer.Serialize(b.PolicyDecisions, Pretty),
        ["execution.trace.json"] = JsonSerializer.Serialize(b.ExecutionTrace, Pretty),
        ["verification.report.json"] = JsonSerializer.Serialize(b.VerificationReport, Pretty),
        ["audit.signed.json"] = JsonSerializer.Serialize(b.SignedAudit, Pretty),
    };

    /// <summary>True iff the bundle's signature matches a fresh signature of its five artifacts.</summary>
    public static bool VerifySignature(TraceBundle b, byte[]? key = null)
    {
        var canonical = string.Join("\n",
            JsonSerializer.Serialize(b.IntentGraph, Canonical),
            JsonSerializer.Serialize(b.PolicyDecisions, Canonical),
            JsonSerializer.Serialize(b.ExecutionTrace, Canonical),
            JsonSerializer.Serialize(b.VerificationReport, Canonical),
            JsonSerializer.Serialize(b.SignedAudit, Canonical));
        return AuditSigner.SignString(canonical, key) == b.BundleSignature;
    }
}
