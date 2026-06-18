using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Tests the interactive confirmation flow: approving a gated (Confirm) node commits its side
/// effect, while the security invariant holds — a blocked zero-trust node can NEVER be approved
/// into execution, no matter what node id is passed.
/// </summary>
public sealed class ConfirmationTests
{
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();
    private const string Prompt1 = "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";
    private const string Prompt2 = "Clean up my downloads and delete anything that looks like junk.";
    private const string Prompt3 = "Summarize the project folder and email the client the important parts.";

    private static string NodeId(RunResult r, string kind) => r.Nodes.First(n => n.Type == kind).Id;

    [Fact]
    public void Without_approval_the_gym_block_is_staged_not_committed()
    {
        var ws = Workspace.CreateDemo();
        var r = Runtime().Run(Prompt1, ws);
        Assert.Equal("NeedsConfirmation", r.Nodes.First(n => n.Type == Kinds.CreateCalendarBlock).Status);
        Assert.All(ws.ProposedBlocks, b => Assert.False(b.Committed));
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Fact]
    public void Approving_the_gym_block_commits_it_and_still_verifies()
    {
        var rt = Runtime();
        var probe = rt.Run(Prompt1, Workspace.CreateDemo());
        var blockId = NodeId(probe, Kinds.CreateCalendarBlock);

        var ws = Workspace.CreateDemo();
        var r = rt.Run(Prompt1, ws, new HashSet<string> { blockId });

        var node = r.Nodes.First(n => n.Type == Kinds.CreateCalendarBlock);
        Assert.True(node.Status is "Executed" or "Verified");
        Assert.Contains(ws.ProposedBlocks, b => b.Committed);
        Assert.All(r.Verification, v => Assert.True(v.Pass));   // committed WITH approval still verifies
    }

    [Fact]
    public void Approving_deletion_actually_deletes_in_the_sandbox()
    {
        var rt = Runtime();
        var delId = NodeId(rt.Run(Prompt2, Workspace.CreateDemo()), Kinds.DeleteFiles);

        var ws = Workspace.CreateDemo();
        var before = ws.Downloads.Count;
        var r = rt.Run(Prompt2, ws, new HashSet<string> { delId });

        Assert.Equal("Verified", r.Nodes.First(n => n.Type == Kinds.DeleteFiles).Status);
        Assert.True(ws.DeletedFiles.Count > 0);
        Assert.Equal(before - ws.DeletedFiles.Count, ws.Downloads.Count);
        Assert.DoesNotContain("DELETE_ME_taxes_2025.pdf", ws.DeletedFiles);  // the important file is never a junk candidate
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Fact]
    public void A_blocked_injected_node_can_NEVER_be_approved()
    {
        var rt = Runtime();
        var probe = rt.Run(Prompt3, Workspace.CreateDemo());
        var injectedId = probe.Nodes.Single(n => n.TrustSource == "RetrievedContent").Id;

        // Maliciously try to approve the injected node (and every node, for good measure).
        var allIds = probe.Nodes.Select(n => n.Id).ToHashSet();
        var ws = Workspace.CreateDemo();
        var r = rt.Run(Prompt3, ws, allIds);

        var injected = r.Nodes.Single(n => n.Id == injectedId);
        Assert.Equal("Blocked", injected.Status);              // still blocked
        Assert.Empty(ws.SentEmails);                           // nothing sent
        Assert.DoesNotContain(ws.Drafts, d => d.RecipientEmail.Contains("attacker"));
        Assert.DoesNotContain(ws.Drafts, d => d.SourceNoteIds.Any(id => ws.Notes.Any(nt => nt.Id == id && nt.Private)));
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }
}
