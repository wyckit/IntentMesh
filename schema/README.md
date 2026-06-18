# IntentMesh Run Artifact Schemas

JSON Schema (draft 2020-12) definitions for the six IntentMesh run artifact files.

## Files

| Schema file | Artifact type | Runtime class |
|---|---|---|
| `intent.graph.schema.json` | Intent graph | `IntentGraphArtifact` |
| `policy.decisions.schema.json` | Policy gate decisions | `PolicyDecisionsArtifact` |
| `execution.trace.schema.json` | Execution trace | `ExecutionTraceArtifact` |
| `verification.report.schema.json` | Postcondition verification | `VerificationReportArtifact` |
| `audit.signed.schema.json` | Signed audit log | `SignedAuditArtifact` |
| `trace.bundle.schema.json` | Complete trace bundle | `TraceBundle` |

## Artifacts

A run produces five separate artifacts that can be exported individually (via `TraceBundleBuilder.SplitFiles`) or carried inline inside a `TraceBundle`:

- **Intent graph** — the resolved node tree from `IntentResolver`, annotated with policy status.
- **Policy decisions** — the `PolicyGate` outcome (allow / block / needsConfirmation) for every node.
- **Execution trace** — what ran, whether it was halted, and what side effects it produced.
- **Verification report** — postcondition check results with expected vs actual state and supporting evidence.
- **Signed audit** — the full ordered audit event log with a SHA-256 hash chain and HMAC signature.

## Naming conventions

All JSON property names are **camelCase** (produced by `System.Text.Json` with `JsonNamingPolicy.CamelCase`).

## Determinism and signing

Bundles are **deterministic**: the same prompt and approvals list (sorted lexicographically) always produce a byte-identical bundle. The `bundleSignature` field in `TraceBundle` is an HMAC-SHA256 over the canonical (minified, camelCase) JSON concatenation of all five artifacts, making the bundle tamper-evident. The `signedAudit` artifact additionally carries a per-event SHA-256 hash chain (`chainHash`) and its own HMAC (`signature`).
