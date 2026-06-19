# Audit Operations

How to run IntentMesh's signed-audit machinery in practice: where keys come from, how to rotate
them, how long to keep runs, and how to verify a stored run hasn't been tampered with.

## What gets persisted

Every run can be exported as a **signed trace bundle** (`TraceBundle`) and persisted by an
`IRunArtifactStore`. The shipped `FileRunArtifactStore` writes, per run, to `runs/{runId}/`:

| Artifact | Role |
|---|---|
| `bundle.json` | **canonical, signed** — the source of truth; signature verifies over all five sections |
| `intent.graph.json` · `policy.decisions.json` · `execution.trace.json` · `verification.report.json` · `audit.signed.json` | **derived exports** for human inspection |

- **`runId` is content-addressed** — the first 16 hex of the bundle signature. The same prompt +
  approvals + key always lands at the same id; a tampered artifact gets a different signature.
- **Approval decisions are persisted in the bundle**: `bundle.Approvals` (the operator's approved
  node ids) plus a `consent` event folded into the signed audit hash chain — so who-approved-what is
  provable from the artifact alone, and replaying with a different approval set changes the signature.
- **Verification outputs** are the `verification.report.json` section (each postcondition + pass/fail).

## Key management

The HMAC signing key is resolved through the `IAuditKeyProvider` seam — it is **not** in the binary.

| Provider | Use |
|---|---|
| `EnvironmentAuditKeyProvider` (default) | reads `INTENTMESH_AUDIT_KEY` (base64 or utf8, **≥128-bit**), optional `INTENTMESH_AUDIT_KEY_ID`. Unset → a clearly-labelled `demo-v1-INSECURE` fallback so demos run. **A production host must assert `IsProductionKey` at startup.** A too-short configured key is rejected (fail-closed). |
| `RotatingAuditKeyProvider` | a current signing key + any prior keys, each addressed by `KeyId` (see Rotation). |

The `KeyId` is recorded in every `SignedAudit` / `SignedAuditArtifact`, so a verifier knows which key
signed an audit and can **refuse a demo-signed audit under a production key**.

**Expectations:** keep `INTENTMESH_AUDIT_KEY` in a secret store (not source); use ≥256-bit random;
never reuse the demo key in production; one key id per key (date-stamp them, e.g. `k-2026-06`).

## Key rotation

Rotating the signing key must **not** invalidate already-signed audits. Use `RotatingAuditKeyProvider`:

```csharp
// New signing key, with the previous key retained so old audits still verify.
var provider = new RotatingAuditKeyProvider(
    currentKeyId: "k-2026-07", currentKey: newKeyBytes,
    priorKeys: new Dictionary<string, byte[]> { ["k-2026-06"] = oldKeyBytes });

// New runs sign with k-2026-07; a run signed under k-2026-06 still verifies by its recorded KeyId:
bool ok = AuditSigner.Verify(loadedSignedAudit, provider);   // resolves the key by signed.KeyId
```

`AuditSigner.Verify(SignedAudit, provider)` looks the key up **by the audit's own `KeyId`** — an
unknown id fails closed. Retire a key only once no live run you need to verify still references it.

Rotation covers the **whole bundle**, not just the audit envelope. A `TraceBundle` records the `KeyId`
that produced its `BundleSignature`, and the full verification path resolves that recorded key:
`TraceBundleBuilder.VerifySignature(bundle, provider)`, `FileRunArtifactStore.VerifyArtifacts(runId, provider)`,
and `RunReplay.Reproduce(runtime, ws, saved, provider)` all take an `IAuditKeyProvider` and verify/replay
a run **under the key it was signed with** — so a run persisted before a rotation still verifies and
reproduces byte-for-byte (regression-tested in `AuditOperationsTests`). Pass a rotation-aware provider,
not a raw key, when old runs may have been signed under a prior key.

**Operationally**, the CLI (`verify-run`) and the Control Room build their provider from the
environment via `AuditKeyProviders.FromEnvironment()`: the current key from `INTENTMESH_AUDIT_KEY` plus
prior keys from **`INTENTMESH_AUDIT_PRIOR_KEYS`** — a `id=base64;id2=base64` list. Keep a rotated-out
key in that list for as long as you need its runs to verify.

## Retention

`FileRunArtifactStore` retains runs explicitly — there is no implicit deletion:

- `Prune(keepNewest)` keeps the N most-recent runs live and **archives the rest** to `runs/.archive/`
  (by on-disk write time). Returns the archived run ids. Archiving preserves verifiability — nothing
  is destroyed.
- `Archive(runId)` archives one run on demand.
- `List()` / `ListSummaries()` return only live (non-archived) runs.

Set a retention policy that matches your obligations (e.g. keep 90 days live, archive older to cold
storage). A cloud `IRunArtifactStore` (S3/Blob) is a drop-in behind the same seam.

## Tamper-verification workflow

Two independent checks, both must pass:

1. **Signature + artifact integrity** — `FileRunArtifactStore.VerifyArtifacts(runId)`: the signed
   `bundle.json` verifies AND every derived split file byte-matches the re-derived bundle (catches
   tampering with any inspectable export, not just the bundle).
2. **Deterministic reproduction** — `RunReplay.Reproduce(runtime, workspace, bundle)`: re-runs the
   same prompt + approvals and requires a byte-identical recomputed signature. A failed signature =
   tampering; a non-reproduction = nondeterminism.

From the CLI:

```
intentmesh verify-run <runsDir> <runId>     # artifact integrity + signature + replay, in one command
intentmesh replay <bundle.json>             # signature + deterministic-replay check for a single bundle
```

Both exit non-zero if any check fails — wire them into CI or a periodic audit job.

## Known limits (see SECURITY_MODEL.md)

The path-safety check is a time-of-check gate (the MCP server's own sandbox is authoritative); the
SSRF guard resolves DNS once at connect; a KMS/HSM-backed `IAuditKeyProvider` and a cloud
`IRunArtifactStore` are seams, not yet shipped implementations.
