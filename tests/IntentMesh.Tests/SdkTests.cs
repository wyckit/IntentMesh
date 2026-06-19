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

    [Fact]
    public void Sdk_persists_and_replays_a_run_through_one_surface()
    {
        var sdk = IntentMeshSdk.Load();
        var dir = Path.Combine(Path.GetTempPath(), "im-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileRunArtifactStore(dir);
            var run = sdk.Run(Prompt, Workspace.CreateDemo());

            var runId = sdk.Save(store, run);                 // step 7 — persist
            Assert.True(store.VerifyArtifacts(runId));         // tamper-evident on disk

            var replay = sdk.Replay(store.Load(runId), Workspace.CreateDemo);
            Assert.True(replay.SignatureVerified);
            Assert.True(replay.Reproduced);                    // deterministic byte-for-byte
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Sdk_explains_what_approval_would_do()
    {
        var sdk = IntentMeshSdk.Load();
        var ex = sdk.Explain("Clean up my downloads and delete anything that looks like junk.", Workspace.CreateDemo);

        Assert.NotEmpty(ex.AwaitingApproval);                  // the destructive delete is queued
        Assert.All(ex.IfApproved, d => Assert.True(d.Changed)); // approval would let it proceed
    }

    [Fact]
    public void RegisteredKinds_is_the_closed_proposable_set()
    {
        var sdk = IntentMeshSdk.Load();
        Assert.All(sdk.RegisteredKinds, k => Assert.True(sdk.IsRegistered(k)));
        Assert.DoesNotContain("act-launch-missiles", sdk.RegisteredKinds);
    }
}
