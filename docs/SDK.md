# IntentMesh Runtime SDK

> Route an existing agent's actions through IntentMesh before any tool runs.

The SDK (`IntentMeshSdk` in `IntentMesh.Core`) is a thin, stable facade over the pipeline:

```
propose → compileGraph → evaluatePolicy → executeTypedAction → verify → exportAudit
```

`Propose` exposes the proposal seam (plug in an LLM). The middle four steps are **deliberately
coupled** inside `Run` — you cannot execute without passing the gate. `Bundle` / `SignAudit` export
the signed audit.

## Quick use

```csharp
using IntentMesh.Core;

var sdk = IntentMeshSdk.Load();                       // loads the im-* TLM bundle
var ws  = Workspace.CreateDemo();                     // your sandboxed tool surface

var run = sdk.Run("Summarize the project folder and email the client the important parts.", ws);
bool ok = run.Verification.All(v => v.Pass);          // postconditions held

var bundle = sdk.Bundle("…", ws);                     // the five signed artifacts
```

## Wrap an existing agent

The model is your proposer; IntentMesh is the authority. Implement `IIntentProposer` to turn your
agent's output into typed intent — the gate validates it, and the model never gains authority:

```csharp
sealed class MyLlmProposer : IIntentProposer
{
    public ProposedPlan Propose(string prompt, Workspace ws)
    {
        // Call your LLM, then map its output to TYPED actions (only registered kinds).
        // The LLM proposes; it does not decide. A bad proposal is gated downstream.
        var nodes = new[]
        {
            new IntentNode {
                Id = "n1", Type = Kinds.DraftEmail, Label = "Draft to client",
                Action = new DraftEmailAction("Acme Client", "Summary", System.Array.Empty<string>()),
                TrustSource = TrustSource.User, Status = NodeStatus.Resolved }
        };
        return new ProposedPlan(nodes, new[] { "my-llm" }, System.Array.Empty<string>());
    }
}

var sdk = IntentMeshSdk.WithProposer(new MyLlmProposer());
var run = sdk.Run(prompt, ws);     // even a rogue proposal (e.g. send-to-attacker) is gated/blocked
```

Proven by `SdkTests.Wrapping_a_rogue_proposer_still_passes_through_the_gate`: a proposer that emits
a send to an attacker is gated (Confirm), never auto-sent.

## Surface

| Method | Step | Returns |
|---|---|---|
| `Propose(prompt, ws)` | propose | `ProposedPlan` (typed candidates) |
| `Run(prompt, ws, approvals?)` | compile + policy + execute + verify | `RunResult` |
| `Bundle(prompt, ws, approvals?)` / `Bundle(run, approvals?)` | export | `TraceBundle` (5 signed artifacts) |
| `SignAudit(run)` | export | `SignedAudit` (tamper-evident) |
| `Save(store, run, approvals?)` | persist | `string` run id (signed, content-addressed) |
| `Replay(savedBundle, freshWs, key?)` | replay | `ReplayResult` (signature + byte-for-byte reproduction) |
| `Explain(prompt, freshWs, approvals?)` | operate | `RunExplanation` (why blocked / what approval would do) |
| `IsRegistered(kind)` / `RegisteredKinds` | — | the closed Translation-Drift-bounded kind set |

`WithProposer(proposer)` swaps the proposal layer; everything downstream is unchanged.

## Full lifecycle, one surface

```csharp
var sdk = IntentMeshSdk.WithProposer(new MyAgentProposer());
var run = sdk.Run(request, ws);                                  // gate · execute · verify
var id  = sdk.Save(store, run);                                  // persist (replayable)
var rep = sdk.Replay(store.Load(id), Workspace.CreateDemo);      // re-verify + reproduce
var why = sdk.Explain(request, Workspace.CreateDemo);            // operator reasoning view
```

## Where to go next

- **[Minimal host template](../templates/IntentMesh.Host.Template/)** — a runnable starting point; copy it and replace the proposer.
- **[EXTENSION-POINTS.md](EXTENSION-POINTS.md)** — every seam (proposer, contract, policy, adapter, verifier, audit, replay) and the invariant each must hold.
- **[INTEGRATIONS.md](INTEGRATIONS.md)** — the MCP proxy, OpenAPI import, and real-adapter examples.
