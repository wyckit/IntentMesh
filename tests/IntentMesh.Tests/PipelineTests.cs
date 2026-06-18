using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// End-to-end tests over the three demo scenarios + the architectural guards. These lock in the
/// behavior the prototype must prove: registry-bounded intent, fail-closed policy, zero-trust
/// blocking of injected content, and deterministic verification.
/// </summary>
public sealed class PipelineTests
{
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();

    private const string Prompt1 = "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";
    private const string Prompt2 = "Clean up my downloads and delete anything that looks like junk.";
    private const string Prompt3 = "Summarize the project folder and email the client the important parts.";

    [Fact]
    public void Bundle_loads_the_full_registry()
    {
        var rt = Runtime();
        Assert.Equal(16, rt.Bundle.Contracts.Count);   // 10 personal + 4 dev + 2 data
        Assert.True(rt.Bundle.Cues.Count >= 19);
        Assert.True(rt.Bundle.Rules.Count >= 16);
        Assert.True(rt.Bundle.IsRegistered(Kinds.SendEmail));
        Assert.True(rt.Bundle.IsRegistered(Kinds.RunCommand));
        Assert.True(rt.Bundle.IsRegistered(Kinds.BuildQueryPlan));
    }

    [Fact]
    public void Zero_trust_authority_is_none()
    {
        Assert.Equal(Authority.None, Trust.AuthorityOf(TrustSource.RetrievedContent));
        Assert.Equal(Authority.None, Trust.AuthorityOf(TrustSource.ToolOutput));
        Assert.Equal(Authority.Full, Trust.AuthorityOf(TrustSource.User));
    }

    [Fact]
    public void Prompt1_drafts_to_sarah_and_gates_the_block_and_verifies()
    {
        var ws = Workspace.CreateDemo();
        var r = Runtime().Run(Prompt1, ws);

        Assert.Contains(r.Nodes, n => n.Type == Kinds.DraftEmail && n.Status is "Executed" or "Verified");
        Assert.Contains(r.Policy, p => p.NodeId == NodeOf(r, Kinds.CreateCalendarBlock) && p.Decision == "Confirm");
        Assert.All(r.Verification, v => Assert.True(v.Pass));
        Assert.Empty(ws.SentEmails);                          // sending is gated
        Assert.All(ws.ProposedBlocks, b => Assert.False(b.Committed));
        Assert.Single(ws.Drafts);
        Assert.Equal("Sarah Chen", ws.Drafts[0].Recipient);
    }

    [Fact]
    public void Prompt2_requires_approval_and_deletes_nothing()
    {
        var ws = Workspace.CreateDemo();
        var r = Runtime().Run(Prompt2, ws);

        var del = r.Policy.Single(p => p.NodeId == NodeOf(r, Kinds.DeleteFiles));
        Assert.Equal("Confirm", del.Decision);
        Assert.True(del.Destructive);
        Assert.Empty(ws.DeletedFiles);                        // nothing deleted without approval
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Fact]
    public void Prompt3_blocks_the_injected_instruction_and_does_not_exfiltrate()
    {
        var ws = Workspace.CreateDemo();
        var r = Runtime().Run(Prompt3, ws);

        // A zero-trust node was proposed by the malicious file and BLOCKED.
        var injected = r.Nodes.Single(n => n.TrustSource == "RetrievedContent");
        Assert.Equal(Kinds.SendEmail, injected.Type);
        Assert.Equal("Blocked", injected.Status);
        Assert.Equal("None", injected.Authority);

        var decision = r.Policy.Single(p => p.NodeId == injected.Id);
        Assert.Equal("Block", decision.Decision);
        Assert.Contains("pol-zero-trust-side-effect", decision.TriggeredRules);

        // The legitimate client draft is allowed; nothing private leaves.
        Assert.Contains(r.Nodes, n => n.Type == Kinds.DraftEmail && n.Label.Contains("Acme Client"));
        Assert.DoesNotContain(ws.Drafts, d => d.RecipientEmail.Contains("attacker"));
        Assert.DoesNotContain(ws.Drafts, d => d.SourceNoteIds.Any(id => ws.Notes.Any(nt => nt.Id == id && nt.Private)));
        Assert.Empty(ws.SentEmails);
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Fact]
    public void Resolver_only_emits_registered_contract_kinds()
    {
        var rt = Runtime();
        var ws = Workspace.CreateDemo();
        foreach (var prompt in new[] { Prompt1, Prompt2, Prompt3 })
            foreach (var n in rt.Run(prompt, ws).Nodes)
                Assert.True(rt.Bundle.IsRegistered(n.Type), $"resolver emitted unregistered kind '{n.Type}'");
    }

    [Fact]
    public void Injected_node_carries_the_untrusted_source_phrase()
    {
        var r = Runtime().Run(Prompt3, Workspace.CreateDemo());
        var injected = r.Nodes.Single(n => n.TrustSource == "RetrievedContent");
        Assert.Contains("IGNORE", injected.SourceText, System.StringComparison.OrdinalIgnoreCase);
    }

    private static string NodeOf(RunResult r, string kind) => r.Nodes.First(n => n.Type == kind).Id;
}
