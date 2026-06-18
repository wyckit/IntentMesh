using System.Text.Json;
using IntentMesh.Core;

namespace IntentMesh.Tests;

/// <summary>
/// Tests for <see cref="AuditExporter"/> — the v0.2 audit trace export feature.
///
/// Verifies structural correctness of the JSON artifact, byte-level determinism across
/// independent runs, and that the Markdown transcript faithfully captures policy decisions
/// (including the zero-trust BLOCK on the injection prompt).
/// </summary>
public sealed class AuditExporterTests
{
    // Mirror PipelineTests prompt constants for consistency.
    private const string Prompt1 = "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";
    private const string Prompt2 = "Clean up my downloads and delete anything that looks like junk.";
    private const string Prompt3 = "Summarize the project folder and email the client the important parts.";

    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();

    // ── ToJson structural correctness ──────────────────────────────────────────────

    [Fact]
    public void ToJson_produces_valid_json_with_prompt_nodes_and_audit_arrays()
    {
        var result = Runtime().Run(Prompt1, Workspace.CreateDemo());
        var json = AuditExporter.ToJson(result);

        // Must be valid JSON — JsonDocument.Parse throws on malformed input.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Prompt is preserved at the top level.
        Assert.Equal(Prompt1, root.GetProperty("prompt").GetString());

        // "nodes" must be a non-empty JSON array.
        var nodes = root.GetProperty("nodes");
        Assert.Equal(JsonValueKind.Array, nodes.ValueKind);
        Assert.True(nodes.GetArrayLength() > 0, "expected at least one node in the nodes array");

        // "audit" must be a non-empty JSON array.
        var audit = root.GetProperty("audit");
        Assert.Equal(JsonValueKind.Array, audit.ValueKind);
        Assert.True(audit.GetArrayLength() > 0, "expected at least one entry in the audit array");

        // Spot-check that each node element has the expected properties.
        foreach (var node in nodes.EnumerateArray())
        {
            Assert.True(node.TryGetProperty("id", out _), "node missing 'id'");
            Assert.True(node.TryGetProperty("type", out _), "node missing 'type'");
            Assert.True(node.TryGetProperty("status", out _), "node missing 'status'");
        }
    }

    [Fact]
    public void ToJson_includes_verification_ids_in_output()
    {
        var result = Runtime().Run(Prompt1, Workspace.CreateDemo());
        var json = AuditExporter.ToJson(result);

        using var doc = JsonDocument.Parse(json);
        var verification = doc.RootElement.GetProperty("verification");
        Assert.Equal(JsonValueKind.Array, verification.ValueKind);

        // Every verification entry present in the RunResult must appear in the JSON.
        foreach (var v in result.Verification)
        {
            var found = verification.EnumerateArray()
                .Any(el => el.TryGetProperty("id", out var idProp) &&
                           idProp.GetString() == v.Id);
            Assert.True(found, $"verification id '{v.Id}' not found in exported JSON");
        }
    }

    [Fact]
    public void ToJson_includes_resolverFired_array()
    {
        var result = Runtime().Run(Prompt2, Workspace.CreateDemo());
        var json = AuditExporter.ToJson(result);

        using var doc = JsonDocument.Parse(json);
        Assert.True(
            doc.RootElement.TryGetProperty("resolverFired", out var fired),
            "JSON must contain a 'resolverFired' property");
        Assert.Equal(JsonValueKind.Array, fired.ValueKind);
    }

    // ── ToJson determinism ─────────────────────────────────────────────────────────

    [Fact]
    public void ToJson_is_deterministic_same_prompt_produces_byte_identical_strings()
    {
        // Two completely independent runtimes and workspaces — only the prompt and
        // symbolic bundle (loaded from disk) are shared.
        var ws1 = Workspace.CreateDemo();
        var ws2 = Workspace.CreateDemo();

        var result1 = Runtime().Run(Prompt1, ws1);
        var result2 = Runtime().Run(Prompt1, ws2);

        var json1 = AuditExporter.ToJson(result1);
        var json2 = AuditExporter.ToJson(result2);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void ToJson_is_deterministic_for_injection_prompt()
    {
        var json1 = AuditExporter.ToJson(Runtime().Run(Prompt3, Workspace.CreateDemo()));
        var json2 = AuditExporter.ToJson(Runtime().Run(Prompt3, Workspace.CreateDemo()));

        Assert.Equal(json1, json2);
    }

    // ── ToMarkdown content correctness ─────────────────────────────────────────────

    [Fact]
    public void ToMarkdown_injection_prompt_contains_block_decision_and_zero_trust_rule()
    {
        var result = Runtime().Run(Prompt3, Workspace.CreateDemo());
        var md = AuditExporter.ToMarkdown(result);

        // The markdown must surface that the injected node was Blocked.
        bool hasBlock = md.Contains("BLOCK", StringComparison.OrdinalIgnoreCase) ||
                        md.Contains("Blocked", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasBlock, "Markdown must contain 'BLOCK' or 'Blocked' for the injection scenario");

        // The triggering rule id must appear in the policy section.
        Assert.Contains("pol-zero-trust-side-effect", md, StringComparison.Ordinal);
    }

    [Fact]
    public void ToMarkdown_contains_expected_section_headings()
    {
        var md = AuditExporter.ToMarkdown(Runtime().Run(Prompt1, Workspace.CreateDemo()));

        Assert.Contains("## Intent Mesh", md, StringComparison.Ordinal);
        Assert.Contains("## Policy Gate", md, StringComparison.Ordinal);
        Assert.Contains("## Execution", md, StringComparison.Ordinal);
        Assert.Contains("## Verification", md, StringComparison.Ordinal);
        Assert.Contains("## Audit Trail", md, StringComparison.Ordinal);
    }

    [Fact]
    public void ToMarkdown_contains_the_prompt_text()
    {
        var md = AuditExporter.ToMarkdown(Runtime().Run(Prompt2, Workspace.CreateDemo()));
        Assert.Contains(Prompt2, md, StringComparison.Ordinal);
    }

    [Fact]
    public void ToMarkdown_is_deterministic()
    {
        var md1 = AuditExporter.ToMarkdown(Runtime().Run(Prompt3, Workspace.CreateDemo()));
        var md2 = AuditExporter.ToMarkdown(Runtime().Run(Prompt3, Workspace.CreateDemo()));

        Assert.Equal(md1, md2);
    }
}
