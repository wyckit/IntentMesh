# IntentMesh — minimal host template

The smallest project that puts IntentMesh **between an agent and its tools**:

```
your agent ──propose──▶ [ IntentMesh: gate · execute · verify · audit ] ──▶ tools
```

## Run it

```bash
dotnet run --project templates/IntentMesh.Host.Template
```

Expected output: the gate decision for the proposed draft, passing postconditions, a persisted
signed run id (tamper-evident), and a reproduced replay.

## Make it yours

1. **Replace `MyAgentProposer`** in `Program.cs` with your agent. Map its output to typed actions of
   **registered kinds only** — `sdk.IsRegistered(kind)` / `sdk.RegisteredKinds` is the closed set.
   Whatever you return is untrusted; the gate validates it. (Try returning a `SendEmailAction` to an
   unknown address — it will be gated/blocked, not executed.)
2. **Swap `Workspace.CreateDemo()`** for your real tool surface via `IToolAdapter`
   (see [ADAPTER-GUIDE.md](../../docs/ADAPTER-GUIDE.md)).
3. **Point the audit key** at your KMS via `IAuditKeyProvider`
   (see [AUDIT-OPERATIONS.md](../../docs/AUDIT-OPERATIONS.md)).

The pipeline downstream of `Propose` never changes — that's the guarantee. Full surface in
[SDK.md](../../docs/SDK.md); every seam in [EXTENSION-POINTS.md](../../docs/EXTENSION-POINTS.md).

> Outside this repo, the host also needs the compiled `im-*.tlmz` TLM bundle on disk (the SDK locates
> it via `DatasetLocator`). Ship the `dataset/compiled` folder or point `IntentMeshSdk.Load(dir)` at it.
