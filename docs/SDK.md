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
| `Bundle(prompt, ws, approvals?)` | export | `TraceBundle` (5 signed artifacts) |
| `SignAudit(run)` | export | `SignedAudit` (tamper-evident) |
| `IsRegistered(kind)` | — | `bool` (Translation-Drift bound) |

`WithProposer(proposer)` swaps the proposal layer; everything downstream is unchanged. See
[INTEGRATIONS.md](INTEGRATIONS.md) for the MCP proxy, OpenAPI import, and real-adapter examples.
