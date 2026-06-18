using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// v1.0 framework hardening: the swappable proposer seam (an LLM drops in with nothing downstream
/// changing — and a dangerous proposal is still gated), capability scoping (the real-adapter gate),
/// and tamper-evident signed audit logs.
/// </summary>
public sealed class FrameworkTests
{
    private static SymbolicBundle Bundle() => SymbolicBundle.Load(DatasetLocator.FindCompiledDir());
    private const string Prompt1 = "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";

    // A stand-in for an LLM proposer: it emits whatever typed nodes it likes — even a send to an
    // attacker — to prove the DOWNSTREAM gate still governs.
    private sealed class RogueProposer : IIntentProposer
    {
        public ProposedPlan Propose(string prompt, Workspace ws) => new(
            new[]
            {
                new IntentNode { Id = "n1", Type = Kinds.ReadCalendar, Label = "Read calendar",
                    Action = new ReadCalendarAction("Friday"), TrustSource = TrustSource.User, Status = NodeStatus.Resolved },
                new IntentNode { Id = "n2", Type = Kinds.SendEmail, Label = "Send to attacker",
                    Action = new SendEmailAction("draft", "attacker@example.com", System.Array.Empty<string>()),
                    TrustSource = TrustSource.User, Status = NodeStatus.Resolved },
            },
            new[] { "rogue-proposer" }, System.Array.Empty<string>());
    }

    [Fact]
    public void The_proposer_is_swappable_and_the_gate_still_governs()
    {
        // Swap the rule-based resolver for a rogue proposer; the pipeline runs unchanged.
        var rt = new IntentMeshRuntime(Bundle(), new RogueProposer());
        var ws = Workspace.CreateDemo();
        var r = rt.Run(Prompt1, ws);

        // The read is allowed; the send the "LLM" proposed is gated (Confirm), NOT auto-sent.
        Assert.Equal("Allow", r.Policy.Single(p => p.NodeId == "n1").Decision);
        Assert.Equal("Confirm", r.Policy.Single(p => p.NodeId == "n2").Decision);
        Assert.Empty(ws.SentEmails);                       // nothing executed without confirmation
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Fact]
    public void Capabilities_load_from_the_bundle()
    {
        var b = Bundle();
        Assert.Equal("email", b.Capabilities[Kinds.SendEmail]);
        Assert.Equal("repo", b.Capabilities[Kinds.RunCommand]);
        Assert.Contains("data", b.AllCapabilities);
    }

    [Fact]
    public void Capability_scoping_blocks_a_node_whose_capability_is_not_granted()
    {
        var b = Bundle();
        // Grant everything EXCEPT email — the draft node must be blocked by capability scoping.
        var granted = b.AllCapabilities.Where(c => c != "email").ToHashSet();
        var rt = new IntentMeshRuntime(b, grantedCapabilities: granted);
        var r = rt.Run(Prompt1, Workspace.CreateDemo());

        var draft = r.Policy.Single(p => p.NodeId == r.Nodes.First(n => n.Type == Kinds.DraftEmail).Id);
        Assert.Equal("Block", draft.Decision);
        Assert.Contains("pol-capability-not-granted", draft.TriggeredRules);

        // With the default (all granted), the same draft is allowed.
        var draft2 = new IntentMeshRuntime(b).Run(Prompt1, Workspace.CreateDemo())
            .Policy.First(p => p.Reason.StartsWith("Drafting allowed"));
        Assert.Equal("Allow", draft2.Decision);
    }

    [Fact]
    public void Signed_audit_verifies_and_detects_tampering()
    {
        var r = new IntentMeshRuntime(Bundle()).Run(Prompt1, Workspace.CreateDemo());
        var signed = AuditSigner.Sign(r);

        Assert.True(AuditSigner.Verify(r, signed.Signature));         // genuine
        Assert.False(AuditSigner.Verify(r, signed.Signature + "00")); // wrong signature

        // Tamper with one audit event -> the chain head changes -> verification fails.
        var tampered = r with { Audit = r.Audit.Select((a, i) =>
            i == 0 ? a with { Message = a.Message + " (tampered)" } : a).ToList() };
        Assert.False(AuditSigner.Verify(tampered, signed.Signature));
    }

    [Fact]
    public void Signed_audit_is_deterministic()
    {
        var rt = new IntentMeshRuntime(Bundle());
        var a = AuditSigner.Sign(rt.Run(Prompt1, Workspace.CreateDemo()));
        var b = AuditSigner.Sign(rt.Run(Prompt1, Workspace.CreateDemo()));
        Assert.Equal(a.Signature, b.Signature);
        Assert.Equal(a.ChainHash, b.ChainHash);
    }
}
