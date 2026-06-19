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
    string BundleSignature,
    // The id of the key that produced BundleSignature, so verification resolves the SAME key (even
    // after rotation) instead of always re-signing with the current key. Defaults to the demo key id
    // so bundles persisted before this field existed still deserialize.
    string KeyId = AuditSigner.DemoKeyId);

public static class TraceBundleBuilder
{
    public const string SchemaVersion = "1.0";

    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static TraceBundle From(RunResult r, IReadOnlyList<string>? approvals = null, byte[]? key = null)
        => From(r, approvals, AuditSigner.Sign(r, key), key);

    /// <summary>Sign the bundle with an explicit key provider — records <c>provider.KeyId</c> so the
    /// run can be verified/replayed under the same key after the current key rotates.</summary>
    public static TraceBundle From(RunResult r, IReadOnlyList<string>? approvals, IAuditKeyProvider provider)
        => From(r, approvals, AuditSigner.Sign(r, provider), provider.GetKey());

    private static TraceBundle From(RunResult r, IReadOnlyList<string>? approvals, SignedAudit signed, byte[]? key)
    {
        var appr = (approvals ?? Array.Empty<string>()).OrderBy(a => a, StringComparer.Ordinal).ToList();
        var ig = new IntentGraphArtifact(SchemaVersion, r.Prompt, r.Nodes);
        var pd = new PolicyDecisionsArtifact(SchemaVersion, r.Policy);
        var et = new ExecutionTraceArtifact(SchemaVersion, r.Execution);
        var vr = new VerificationReportArtifact(SchemaVersion, r.Verification.All(v => v.Pass), r.Verification);
        var sa = new SignedAuditArtifact(SchemaVersion, r.Audit, signed.ChainHash, signed.Signature, signed.KeyId);

        // The bundle signature covers the canonical JSON of all five artifacts, under the same key id
        // the audit recorded — so verification later resolves THAT key, not just the current one.
        var bundleSig = AuditSigner.SignString(Canonicalize(ig, pd, et, vr, sa), key);
        return new TraceBundle(SchemaVersion, r.Prompt, appr, r.Summary, ig, pd, et, vr, sa, bundleSig, signed.KeyId);
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

    private static string Canonicalize(TraceBundle b)
        => Canonicalize(b.IntentGraph, b.PolicyDecisions, b.ExecutionTrace, b.VerificationReport, b.SignedAudit);

    private static string Canonicalize(IntentGraphArtifact ig, PolicyDecisionsArtifact pd, ExecutionTraceArtifact et,
        VerificationReportArtifact vr, SignedAuditArtifact sa)
        => string.Join("\n",
            JsonSerializer.Serialize(ig, Canonical),
            JsonSerializer.Serialize(pd, Canonical),
            JsonSerializer.Serialize(et, Canonical),
            JsonSerializer.Serialize(vr, Canonical),
            JsonSerializer.Serialize(sa, Canonical));

    /// <summary>Verify the bundle signature with an explicit raw key (e.g. a test key). When
    /// <paramref name="key"/> is null, resolve the key the bundle recorded against the process-default
    /// provider — fail-closed if that provider doesn't hold the recorded key id (e.g. after rotation,
    /// pass an <see cref="IAuditKeyProvider"/> that carries the prior key instead).</summary>
    public static bool VerifySignature(TraceBundle b, byte[]? key = null)
        => key is not null
            ? AuditSigner.SignString(Canonicalize(b), key) == b.BundleSignature
            : VerifySignature(b, AuditSigner.Default);

    /// <summary>Rotation-aware verify: resolve the key the bundle was signed under by its recorded
    /// <c>KeyId</c> (a rotatable provider supplies the historical key), then re-sign and compare. An
    /// unknown key id fails closed.</summary>
    public static bool VerifySignature(TraceBundle b, IAuditKeyProvider provider)
    {
        var key = AuditSigner.ResolveKey(b.KeyId, provider);
        return key is not null && AuditSigner.SignString(Canonicalize(b), key) == b.BundleSignature;
    }
}
