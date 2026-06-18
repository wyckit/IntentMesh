using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntentMesh.Core;

/// <summary>
/// Produces deterministic, exportable audit artifacts from a completed <see cref="RunResult"/>.
///
/// This is the agentic analog of PassGen's trace: a signable, replayable record of exactly what
/// the pipeline resolved, gated, executed, and verified for a given prompt. Because both output
/// formats contain no timestamps and no randomness, two runs of the same prompt over the same
/// <see cref="Workspace"/> state produce byte-identical output — making the artifact suitable for
/// hash-based integrity checks, diff-based regression tests, and legal audit trails.
///
/// v0.2 roadmap item — wire into the Web Control Room and CLI --export flag after delivery.
/// </summary>
public static class AuditExporter
{
    // Reuse a single options instance — JsonSerializerOptions is thread-safe once constructed.
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Serialises the full <see cref="RunResult"/> to an indented, camelCase JSON document.
    ///
    /// The document contains every field of every sub-record (prompt, resolverFired, unsupported,
    /// nodes, policy, execution, verification, audit, summary) so it is fully self-contained for
    /// replay or signature verification. Property order is determined by the record declaration
    /// order in <c>RunResult.cs</c> and is stable across runs.
    /// </summary>
    /// <param name="result">The completed pipeline run to export.</param>
    /// <returns>
    /// An indented JSON string. Two calls with structurally identical <paramref name="result"/>
    /// values produce byte-identical strings.
    /// </returns>
    public static string ToJson(RunResult result)
        => JsonSerializer.Serialize(result, _jsonOptions);

    /// <summary>
    /// Renders the <see cref="RunResult"/> as a human-readable Markdown transcript.
    ///
    /// Sections mirror the pipeline stages in order: Intent Mesh, Policy Gate, Execution,
    /// Verification, and Audit Trail. The output is fully deterministic — no timestamps,
    /// no random identifiers — so two calls with structurally identical data produce identical
    /// Markdown.
    /// </summary>
    /// <param name="result">The completed pipeline run to export.</param>
    /// <returns>A UTF-8 Markdown string suitable for display, archiving, or diff-based testing.</returns>
    public static string ToMarkdown(RunResult result)
    {
        var sb = new StringBuilder();

        // ── Title ──────────────────────────────────────────────────────────────────
        sb.AppendLine("# IntentMesh Audit Transcript");
        sb.AppendLine();
        sb.AppendLine($"**Prompt:** {EscapeMd(result.Prompt)}");
        sb.AppendLine();

        if (result.ResolverFired.Count > 0)
        {
            sb.AppendLine($"**Resolvers fired:** {string.Join(", ", result.ResolverFired)}");
            sb.AppendLine();
        }

        if (result.Unsupported.Count > 0)
        {
            sb.AppendLine($"**Unsupported intents:** {string.Join(", ", result.Unsupported)}");
            sb.AppendLine();
        }

        // ── Summary bar ────────────────────────────────────────────────────────────
        var s = result.Summary;
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"| Total | Allowed | Needs Confirmation | Blocked | Executed | Verified |");
        sb.AppendLine($"|------:|--------:|-------------------:|--------:|---------:|---------:|");
        sb.AppendLine($"| {s.Total} | {s.Allowed} | {s.NeedsConfirmation} | {s.Blocked} | {s.Executed} | {s.Verified} |");
        sb.AppendLine();

        // ── Intent Mesh ────────────────────────────────────────────────────────────
        sb.AppendLine("## Intent Mesh");
        sb.AppendLine();
        if (result.Nodes.Count == 0)
        {
            sb.AppendLine("_No nodes resolved._");
        }
        else
        {
            sb.AppendLine("| Id | Type | Label | Status | Trust Source |");
            sb.AppendLine("|----|------|-------|--------|--------------|");
            foreach (var n in result.Nodes)
            {
                sb.AppendLine(
                    $"| {EscapeMd(n.Id)} | {EscapeMd(n.Type)} | {EscapeMd(n.Label)} " +
                    $"| {EscapeMd(n.Status)} | {EscapeMd(n.TrustSource)} |");
            }
        }

        sb.AppendLine();

        // ── Policy Gate ────────────────────────────────────────────────────────────
        sb.AppendLine("## Policy Gate");
        sb.AppendLine();
        if (result.Policy.Count == 0)
        {
            sb.AppendLine("_No policy decisions recorded._");
        }
        else
        {
            sb.AppendLine("| Node | Decision | Risk | Reason | Triggered Rules |");
            sb.AppendLine("|------|----------|------|--------|-----------------|");
            foreach (var p in result.Policy)
            {
                var rules = p.TriggeredRules.Count > 0
                    ? string.Join(", ", p.TriggeredRules)
                    : "—";
                sb.AppendLine(
                    $"| {EscapeMd(p.NodeId)} | **{EscapeMd(p.Decision)}** | {EscapeMd(p.Risk)} " +
                    $"| {EscapeMd(p.Reason)} | {EscapeMd(rules)} |");
            }
        }

        sb.AppendLine();

        // ── Execution ──────────────────────────────────────────────────────────────
        sb.AppendLine("## Execution");
        sb.AppendLine();
        if (result.Execution.Count == 0)
        {
            sb.AppendLine("_No execution records._");
        }
        else
        {
            sb.AppendLine("| Node | Label | Ran | Halted | Summary |");
            sb.AppendLine("|------|-------|-----|--------|---------|");
            foreach (var e in result.Execution)
            {
                sb.AppendLine(
                    $"| {EscapeMd(e.NodeId)} | {EscapeMd(e.Label)} " +
                    $"| {BoolMd(e.Ran)} | {BoolMd(e.Halted)} | {EscapeMd(e.Summary)} |");
            }
        }

        sb.AppendLine();

        // ── Verification ───────────────────────────────────────────────────────────
        sb.AppendLine("## Verification");
        sb.AppendLine();
        if (result.Verification.Count == 0)
        {
            sb.AppendLine("_No verification checks._");
        }
        else
        {
            sb.AppendLine("| Id | Result | Expected | Actual | Evidence |");
            sb.AppendLine("|----|--------|----------|--------|----------|");
            foreach (var v in result.Verification)
            {
                var badge = v.Pass ? "PASS" : "FAIL";
                sb.AppendLine(
                    $"| {EscapeMd(v.Id)} | **{badge}** | {EscapeMd(v.Expected)} " +
                    $"| {EscapeMd(v.Actual)} | {EscapeMd(v.Evidence)} |");
            }
        }

        sb.AppendLine();

        // ── Audit Trail ────────────────────────────────────────────────────────────
        sb.AppendLine("## Audit Trail");
        sb.AppendLine();
        if (result.Audit.Count == 0)
        {
            sb.AppendLine("_Empty audit trail._");
        }
        else
        {
            sb.AppendLine("| Seq | Node | Phase | Message |");
            sb.AppendLine("|----:|------|-------|---------|");
            foreach (var a in result.Audit.OrderBy(x => x.Seq))
            {
                sb.AppendLine(
                    $"| {a.Seq} | {EscapeMd(a.NodeId)} | {EscapeMd(a.Phase)} | {EscapeMd(a.Message)} |");
            }
        }

        sb.AppendLine();

        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    /// <summary>Escapes pipe characters so they don't break Markdown table cells.</summary>
    private static string EscapeMd(string? text)
        => (text ?? string.Empty).Replace("|", "\\|");

    private static string BoolMd(bool value) => value ? "yes" : "no";
}
