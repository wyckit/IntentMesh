using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>The Runtime SDK facade: a builder can run a prompt to a verified result + signed bundle,
/// and can wrap their own (LLM) proposer — which still passes through the gate, never gaining authority.</summary>
public sealed class SdkTests
{
    private const string Prompt = "Summarize the project folder and email the client the important parts.";

    [Fact]
    public void Sdk_runs_a_prompt_to_a_verified_signed_bundle()
    {
        var sdk = IntentMeshSdk.Load();
        var run = sdk.Run(Prompt, Workspace.CreateDemo());
        Assert.All(run.Verification, v => Assert.True(v.Pass));

        var bundle = sdk.Bundle(Prompt, Workspace.CreateDemo());
        Assert.NotEmpty(bundle.BundleSignature);
        Assert.True(TraceBundleBuilder.VerifySignature(bundle));
    }

    [Fact]
    public void Propose_only_emits_registered_kinds()
    {
        var sdk = IntentMeshSdk.Load();
        var plan = sdk.Propose(Prompt, Workspace.CreateDemo());
        Assert.All(plan.Nodes, n => Assert.True(sdk.IsRegistered(n.Type)));
    }

    // A stand-in LLM proposer that proposes a send to an attacker — the gate must still block it.
    private sealed class RogueProposer : IIntentProposer
    {
        public ProposedPlan Propose(string prompt, Workspace ws) => new(
            new[]
            {
                new IntentNode { Id = "n1", Type = Kinds.SendEmail, Label = "Send to attacker",
                    Action = new SendEmailAction("draft", "attacker@example.com", System.Array.Empty<string>()),
                    TrustSource = TrustSource.User, Status = NodeStatus.Resolved }
            },
            new[] { "rogue" }, System.Array.Empty<string>());
    }

    [Fact]
    public void Wrapping_a_rogue_proposer_still_passes_through_the_gate()
    {
        var sdk = IntentMeshSdk.WithProposer(new RogueProposer());
        var ws = Workspace.CreateDemo();
        var run = sdk.Run(Prompt, ws);
        // The "LLM" proposed a send; it is gated (Confirm), not auto-sent.
        Assert.Equal("Confirm", run.Policy.Single(p => p.NodeId == "n1").Decision);
        Assert.Empty(ws.SentEmails);
    }
}
