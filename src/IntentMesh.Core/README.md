# IntentMesh.Core

The verified-intent runtime kernel — the safety layer an AI agent runs *through* before it touches a
real tool. Raw language never reaches a tool; it must become typed, validated, authorized intent first.

```
propose → compile intent graph → policy gate → typed adapters → postcondition verifier → signed audit
```

```csharp
using IntentMesh.Core;

var sdk = IntentMeshSdk.Load();              // loads the im-* TLM bundle
var run = sdk.Run("Summarize the project folder and email the client the important parts.",
                  Workspace.CreateDemo());
bool ok = run.Verification.All(v => v.Pass); // postconditions held; nothing the gate blocked executed

var id  = sdk.Save(store, run);              // persist a signed, replayable bundle
var rep = sdk.Replay(store.Load(id), Workspace.CreateDemo);  // re-verify + reproduce byte-for-byte
```

Wrap your own agent by implementing `IIntentProposer` — the model proposes; the policy gate is the
authority and the model never gains it. Full surface and seams: see the
[SDK guide](https://github.com/wyckit/IntentMesh/blob/master/docs/SDK.md) and
[EXTENSION-POINTS](https://github.com/wyckit/IntentMesh/blob/master/docs/EXTENSION-POINTS.md).

> v1.7.0 — verified-intent platform **preview**. Research/SDK preview, not production infrastructure;
> see [MATURITY.md](https://github.com/wyckit/IntentMesh/blob/master/docs/MATURITY.md) for the
> production-ready / experimental / future split.
