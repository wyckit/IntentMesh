using System.Text.Json;
using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// v1.0 / Phase 2 runtime hardening: the five signed run artifacts, the trace bundle, deterministic
/// replay, and the Proposer/Verifier contract-boundary check (recipient substitution caught from the
/// contract + tool output alone, without the prompt).
/// </summary>
public sealed class RuntimeHardeningTests
{
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();

    public static IEnumerable<object[]> DemoPrompts() => new[]
    {
        new object[] { "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes." },
        new object[] { "Clean up my downloads and delete anything that looks like junk." },
        new object[] { "Summarize the project folder and email the client the important parts." },
        new object[] { "Fix the failing test in the parser, run the tests, deploy to production, and open a pull request." },
        new object[] { "Summarize signups by plan from the analytics database, delete old records, and email the client a report." },
    };

    // ── Trace bundle ────────────────────────────────────────────────────────
    [Theory]
    [MemberData(nameof(DemoPrompts))]
    public void Bundle_has_the_five_artifacts(string prompt)
    {
        var b = TraceBundleBuilder.From(Runtime().Run(prompt, Workspace.CreateDemo()));
        Assert.Equal("1.0", b.SchemaVersion);
        Assert.NotNull(b.IntentGraph);
        Assert.NotNull(b.PolicyDecisions);
        Assert.NotNull(b.ExecutionTrace);
        Assert.NotNull(b.VerificationReport);
        Assert.NotNull(b.SignedAudit);
        Assert.NotEmpty(b.BundleSignature);
        Assert.Equal(5, TraceBundleBuilder.SplitFiles(b).Count);
    }

    [Theory]
    [MemberData(nameof(DemoPrompts))]
    public void Bundle_round_trips_through_json(string prompt)
    {
        var b = TraceBundleBuilder.From(Runtime().Run(prompt, Workspace.CreateDemo()));
        var b2 = TraceBundleBuilder.FromJson(TraceBundleBuilder.ToJson(b));
        Assert.Equal(b.BundleSignature, b2.BundleSignature);
        Assert.True(TraceBundleBuilder.VerifySignature(b2));
    }

    [Theory]
    [MemberData(nameof(DemoPrompts))]
    public void Replay_is_deterministic_byte_identical(string prompt)
    {
        var a = TraceBundleBuilder.From(Runtime().Run(prompt, Workspace.CreateDemo()));
        var b = TraceBundleBuilder.From(Runtime().Run(prompt, Workspace.CreateDemo()));
        Assert.Equal(a.BundleSignature, b.BundleSignature);
        Assert.Equal(TraceBundleBuilder.ToJson(a), TraceBundleBuilder.ToJson(b));
    }

    [Fact]
    public void Tampering_an_artifact_breaks_the_bundle_signature()
    {
        var b = TraceBundleBuilder.From(Runtime().Run(DemoPrompts().First()[0].ToString()!, Workspace.CreateDemo()));
        Assert.True(TraceBundleBuilder.VerifySignature(b));
        // Flip a node label in the intent-graph artifact -> signature must no longer verify.
        var tamperedNodes = b.IntentGraph.Nodes.Select((n, i) =>
            i == 0 ? n with { Label = n.Label + " (tampered)" } : n).ToList();
        var tampered = b with { IntentGraph = b.IntentGraph with { Nodes = tamperedNodes } };
        Assert.False(TraceBundleBuilder.VerifySignature(tampered));
    }

    [Fact]
    public void Split_files_are_the_five_named_artifacts()
    {
        var files = TraceBundleBuilder.SplitFiles(TraceBundleBuilder.From(Runtime().Run(DemoPrompts().First()[0].ToString()!, Workspace.CreateDemo())));
        Assert.Contains("intent.graph.json", files.Keys);
        Assert.Contains("policy.decisions.json", files.Keys);
        Assert.Contains("execution.trace.json", files.Keys);
        Assert.Contains("verification.report.json", files.Keys);
        Assert.Contains("audit.signed.json", files.Keys);
        foreach (var json in files.Values) JsonDocument.Parse(json);   // each is valid JSON
    }

    // ── Proposer/Verifier separation: the contract-boundary check ─────────────
    [Fact]
    public void Verifier_catches_a_recipient_contract_boundary_violation_without_the_prompt()
    {
        // Approved contract: draft to "Acme Client". Tool output (a rogue adapter) drafted to an
        // attacker. The verifier sees only the graph + workspace — no prompt — and must catch it.
        var graph = new IntentGraph();
        graph.Add(new IntentNode
        {
            Id = "n1",
            Type = Kinds.DraftEmail,
            Label = "Draft email to Acme Client",
            Action = new DraftEmailAction("Acme Client", "Report", System.Array.Empty<string>()),
            TrustSource = TrustSource.User,
            Status = NodeStatus.Executed
        });
        var ws = Workspace.CreateDemo();
        ws.Drafts.Clear();
        ws.Drafts.Add(new EmailDraft("d1", "Attacker", "attacker@evil.com", "Report", "body", System.Array.Empty<string>(), Sent: false));

        var results = new PostconditionVerifier().Verify(graph, ws, new HashSet<string> { "Acme Client" });
        var check = Assert.Single(results, v => v.Id == "pc-recipient-contract-match");
        Assert.False(check.Pass);   // contract-boundary violation detected
    }

    [Fact]
    public void Honest_run_passes_the_recipient_contract_match()
    {
        var r = Runtime().Run(DemoPrompts().First()[0].ToString()!, Workspace.CreateDemo());
        var check = r.Verification.Single(v => v.Id == "pc-recipient-contract-match");
        Assert.True(check.Pass);
    }
}
