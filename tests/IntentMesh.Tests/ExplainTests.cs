using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Tests for RunExplain — the operator "approval queue / why blocked / what would approval do" view.
/// It must surface the gated set with its policy evidence, and its what-if projection must be a real
/// kernel re-run (so a blocked node provably STAYS blocked when approval is granted).
/// </summary>
public sealed class ExplainTests
{
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();

    private const string DeletePrompt = "Clean up my downloads and delete anything that looks like junk.";
    private const string InjectionPrompt = "Summarize the project folder and email the client the important parts.";

    [Fact]
    public void Explain_surfaces_the_approval_queue_and_projects_what_approval_would_do()
    {
        var ex = RunExplain.Explain(Runtime(), DeletePrompt, Workspace.CreateDemo);

        // The destructive delete is gated, with its policy evidence attached.
        var delete = Assert.Single(ex.AwaitingApproval, g => g.Type == Kinds.DeleteFiles);
        Assert.Equal("Confirm", delete.Decision);
        Assert.NotEmpty(delete.TriggeredRules);

        // What-if: every queued approval projects a real status change (NeedsConfirmation → proceeded).
        Assert.Equal(ex.AwaitingApproval.Count, ex.IfApproved.Count);
        Assert.All(ex.IfApproved, d =>
        {
            Assert.Equal("NeedsConfirmation", d.Before);
            Assert.True(d.Changed, $"approving {d.NodeId} should change its outcome, got {d.After}");
        });
    }

    [Fact]
    public void Explain_lists_blocked_nodes_with_evidence_and_never_offers_to_approve_them()
    {
        var ex = RunExplain.Explain(Runtime(), InjectionPrompt, Workspace.CreateDemo);

        // The injected zero-trust send is blocked, and the triggering rule is shown as evidence.
        var injected = Assert.Single(ex.Blocked, g => g.Type == Kinds.SendEmail);
        Assert.Equal("Block", injected.Decision);
        Assert.Contains("pol-zero-trust-side-effect", injected.TriggeredRules);

        // A blocked node is NEVER in the approval queue — approval can't lift a hard block (fail-closed).
        Assert.DoesNotContain(ex.AwaitingApproval, g => g.NodeId == injected.NodeId);
        Assert.DoesNotContain(ex.IfApproved, d => d.NodeId == injected.NodeId);
    }
}
