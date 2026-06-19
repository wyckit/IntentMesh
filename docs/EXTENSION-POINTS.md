# IntentMesh Extension Points

> The seams you implement to wrap your own agent, tools, model, and infrastructure. Everything else
> — the graph compile, the policy gate, verification, the signed audit — is fixed and runs the same
> regardless of what you plug in. **The model proposes; the gate is the authority.**

Start from the [minimal host template](../templates/IntentMesh.Host.Template/) and the
[SDK guide](SDK.md). This page is the reference for *what* is pluggable and the invariant each seam
must preserve.

## The pipeline, and where you plug in

```
  [IIntentProposer] ─▶ compile graph ─▶ [PolicyGate] ─▶ [IToolAdapter] ─▶ [PostconditionVerifier] ─▶ signed audit
       ▲ you                (fixed)        ▲ authoritative    ▲ you              ▲ you                 (fixed)
```

You never replace the middle. You supply the edges (propose, execute) and the rules (policy, verify).

## The five core seams

| Seam | Interface / type | Implement it to… | Invariant you MUST preserve |
|---|---|---|---|
| **Proposer** | `IIntentProposer` | Turn an agent/LLM's output into typed intent candidates | Emit only **registered kinds** (`sdk.IsRegistered`); proposals are untrusted and gated. The model never decides. |
| **Contracts** | TLM bundle (`im-*.tlmz`) | Declare the typed actions, their fields, risk, side-effect, capability | A kind not in the registry cannot be proposed or executed (Translation-Drift bound). Authored via `IntentMesh.Tlm.Cli`. |
| **Policy** | `PolicyGate` (C# is authoritative) + symbolic rule metadata | Decide Allow / Confirm / Block per node | Fail-closed: the gate is the only authority; a blocked node never executes. See [POLICY-REVIEW.md](POLICY-REVIEW.md). |
| **Adapter** | `IToolAdapter` | Actually run an approved typed action against a real tool | Declare a capability; never act without approval; enforce action invariants (e.g. draft-before-send) in *every* adapter. See [ADAPTER-GUIDE.md](ADAPTER-GUIDE.md). |
| **Verifier** | `PostconditionVerifier` | Assert the world matches the approved intent after execution | Zero-trust postconditions must hold (e.g. a blocked side-effect produced no effect). |

## Infrastructure seams

| Seam | Interface | Implement it to… |
|---|---|---|
| **LLM transport** | `ILlmClient` | Back `LlmIntentProposer` with a real model (`AnthropicLlmClient` ships). Expose `Provenance` so proposed nodes are traceable. |
| **MCP transport** | `IMcpClient` | Front any MCP server (stdio / HTTP / SSE ship). Wrap with `RetryingMcpClient` for transient resilience. |
| **Audit key** | `IAuditKeyProvider` / `IRotatableAuditKeyProvider` | Supply the signing key from your KMS/HSM and support rotation. See [AUDIT-OPERATIONS.md](AUDIT-OPERATIONS.md). |
| **Run store** | `IRunArtifactStore` | Persist signed bundles to a DB/blob sink instead of files (`FileRunArtifactStore` ships). |

## What you do NOT extend

- **The graph compile, the gate evaluation, verification, the audit hash-chain.** These are the
  guarantees; making them pluggable would make them defeatable. The gate sits *after* your proposer
  and *before* your adapter by construction — you cannot route around it.

## The full lifecycle through one surface

`IntentMeshSdk` is the stable facade — you can drive the entire story without touching internals:

```csharp
var sdk   = IntentMeshSdk.WithProposer(new MyAgentProposer());   // your seam
var run   = sdk.Run(request, ws);                                // gate · execute · verify
var id    = sdk.Save(store, run);                                // persist (signed, replayable)
var ok    = sdk.Replay(store.Load(id), Workspace.CreateDemo);    // re-verify + reproduce
var why   = sdk.Explain(request, Workspace.CreateDemo);          // why blocked / what approval would do
```

See [SDK.md](SDK.md) for the method table and [INTEGRATIONS.md](INTEGRATIONS.md) for the MCP proxy,
OpenAPI import, and OAuth examples.
