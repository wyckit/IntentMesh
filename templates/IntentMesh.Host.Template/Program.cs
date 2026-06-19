using IntentMesh.Core;

// ─────────────────────────────────────────────────────────────────────────────
// IntentMesh — minimal host template
//
// This is the smallest end-to-end host that sits BETWEEN an agent and its tools:
//
//     your agent ──propose──▶ [ IntentMesh: gate · execute · verify · audit ] ──▶ tools
//
// The whole story runs through one stable surface — IntentMeshSdk. You do NOT need to
// understand the rest of the codebase to wrap an agent: implement IIntentProposer, then
// call Run / Save / Replay / Explain. The model proposes; the gate is the authority.
// ─────────────────────────────────────────────────────────────────────────────

// 1. Load the SDK over the compiled TLM bundle, with YOUR agent as the proposer.
//    (Use IntentMeshSdk.Load() for the built-in rule-based proposer instead.)
var sdk = IntentMeshSdk.WithProposer(new MyAgentProposer());

// 2. Your sandboxed tool surface. Replace Workspace.CreateDemo() with your own adapters.
var ws = Workspace.CreateDemo();
const string request = "Draft the client a project summary.";

// 3. Run: compile the intent graph → evaluate policy → execute only validated typed actions →
//    verify postconditions. Nothing the gate blocks ever executes.
var run = sdk.Run(request, ws);

Console.WriteLine($"Request: {request}\n");
foreach (var p in run.Policy)
    Console.WriteLine($"  [{p.Decision,-7}] {p.Label}  — {p.Reason}");
Console.WriteLine($"\n  postconditions: {(run.Verification.All(v => v.Pass) ? "ALL PASS" : "FAILED")}");

// 4. Persist the run as a signed, replayable, content-addressed bundle (audit operations).
var store = new FileRunArtifactStore(Path.Combine(AppContext.BaseDirectory, "runs"));
var runId = sdk.Save(store, run);
Console.WriteLine($"\n  persisted run: {runId}  (tamper-evident: {store.VerifyArtifacts(runId)})");

// 5. Replay it: re-verify the signature and re-run deterministically — proof the audit is trustworthy.
var replay = sdk.Replay(store.Load(runId), Workspace.CreateDemo);
Console.WriteLine($"  replay: signature {(replay.SignatureVerified ? "OK" : "FAIL")}, reproduced {(replay.Reproduced ? "OK" : "FAIL")}");

// 6. Explain: what is gated and what granting approval would change (a blocked node stays blocked).
var explain = sdk.Explain(request, Workspace.CreateDemo);
foreach (var g in explain.AwaitingApproval)
    Console.WriteLine($"  awaiting approval: {g.Label} — {g.Reason}");

// ─────────────────────────────────────────────────────────────────────────────
// YOUR AGENT lives here. Map its output to TYPED intent of REGISTERED kinds only.
// Whatever you return is untrusted: the gate validates it and the model never gains authority.
// Try changing the action to a SendEmailAction to an unknown address — it will be gated/blocked,
// not executed, no matter what this proposer claims.
// ─────────────────────────────────────────────────────────────────────────────
sealed class MyAgentProposer : IIntentProposer
{
    public ProposedPlan Propose(string prompt, Workspace ws)
    {
        // Call your LLM/agent here, then translate its output into typed actions. This template
        // hardcodes one safe draft so it runs offline; swap in your real mapping.
        var node = new IntentNode
        {
            Id = "n1",
            Type = Kinds.DraftEmail,
            Label = "Draft project summary to client",
            Action = new DraftEmailAction("Acme Client", "Project summary", Array.Empty<string>()),
            TrustSource = TrustSource.User,   // proposed AS the user — still gated
            Status = NodeStatus.Resolved,
        };
        return new ProposedPlan(new[] { node }, new[] { "my-agent" }, Array.Empty<string>());
    }
}
