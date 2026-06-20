# Changelog

All notable changes to IntentMesh. Claims are test-backed; see [docs/MATURITY.md](docs/MATURITY.md)
for the production-ready / experimental / future breakdown.

## v1.8.0 — Security review hardening pass

Closes a full external review (6 High, 7 Medium) plus release hygiene. **183 passing + 3 env-gated
skipped.** No conceptual changes to the kernel — these harden correctness, trust boundaries, and
release engineering.

### Security / correctness (High)
- **Signed-audit transcript binding (H1):** `AuditSigner.Verify(SignedAudit)` recomputes the chain
  from the events in `AuditJson` — editing the exported transcript while keeping chainHash/signature
  no longer verifies.
- **User-requested recipients (H2):** derived as ground truth from the prompt + workspace contacts,
  not from proposer output — a bad proposer can't invent a recipient that `pc-recipient-matches-request` trusts.
- **MCP HTTP SSRF (H3):** the default client disables redirects and re-validates the resolved IP at
  connect time (closes redirect + DNS-rebinding bypass).
- **Control Room access (H4):** the `/api` surface is enforced local-only (loopback) unless
  `INTENTMESH_WEB_TOKEN` is set (then required on every call).
- **True per-file approval (H5):** approving one file (`node#fileRef`) deletes only that file; a single
  node approval can't delete the whole batch.
- **Self-contained packages (H6):** the compiled TLM bundle is embedded in `IntentMesh.Core`, so
  `IntentMeshSdk.Load()` works from the package with no `dataset/compiled` on disk.

### Correctness (Medium)
- **Approved-but-halted (M7):** an allowed/approved action that halts is marked `Halted`, not `Allowed`.
- **Direct run-query gating (M8):** a direct `RunQueryAction` is validated by the data policy (untrusted
  origin blocked, table existence) — no generic-allow bypass.
- **OpenAPI hints (M9):** an untrusted/imported spec can't downgrade a mutating op to side-effect
  "none" to silence confirmation (`trusted` flag, default off).
- **Stdio cleanup (M10):** a failed handshake disposes (kills) the child process; stderr is drained.
- **Artifact viewer (M11):** serves the persisted split file as stored (reflects tampering), not a
  regenerated copy.
- **Honest benchmark (M12):** IntentBench is labeled an architecture demonstration (measured mesh,
  modeled baselines); the dev "legit task" criterion now requires a PR drafted.
- **Test skip semantics (M13):** env/dependency-gated tests skip (`Xunit.SkippableFact`) instead of
  silently passing — the suite reports 183 passing + 3 skipped.

### Release hygiene
- Version → 1.8.0; explicit `LICENSE.txt` shipped in every package (no ambiguous license metadata);
  `packages.lock.json` committed + CI `--locked-mode`; CI uploads package artifacts and validates
  tag == `<Version>` on release tags.

## v1.7.1 — Post-release hardening

Review fixes after the v1.7.0 tag (no API changes):
- **Audit verification loads the bundle once.** `FileRunArtifactStore.VerifyArtifacts` previously read
  `bundle.json` twice (once for the signature, once for the split-file comparison); a racing
  modification could make the two checks judge different bundles. It now loads once and verifies that
  single in-memory bundle.
- **Prior rotation keys are validated.** `RotatingAuditKeyProvider` now enforces the 128-bit floor on
  every PRIOR key (not just the current one), and `AuditKeyProviders.FromEnvironment` fails loudly on a
  malformed `INTENTMESH_AUDIT_PRIOR_KEYS` entry (bad base64 / missing `id=`) instead of silently
  dropping it — a sub-128-bit or malformed prior key can no longer be accepted for verification.
- **E2E strict mode.** `INTENTMESH_REQUIRE_REAL_MCP=1` makes the full-path E2E fail (instead of
  silently using the in-process fake) when the real stdio MCP leg is unavailable; CI sets it so the
  release gate genuinely exercises real stdio MCP.

## v1.7.0 — Verified-intent **platform** (adoptable by outside builders)

The transition from research-grade prototype to an adoptable runtime platform: usable by outside
builders, credible against real agents, and operationally clear for tool governance. **176/176 tests.**

### Area 1 — Productize the runtime surface
- `IntentMeshSdk` now covers the full lifecycle through one stable facade: `Bundle(run)`,
  `Save(store, run)`, `Replay(savedBundle, freshWs)`, `Explain(prompt, freshWs)`, `RegisteredKinds`.
- **Minimal host template** (`templates/IntentMesh.Host.Template`) — the smallest runnable project
  that puts IntentMesh between an agent and its tools. Builds and runs.
- New [EXTENSION-POINTS.md](docs/EXTENSION-POINTS.md); refreshed [SDK.md](docs/SDK.md).

### Area 2 — Policy authoring made real
- `PolicyCatalog` (rule listing + diffing), `PolicyFixtures` (author/test/version rules), the
  `policy list | fixtures | diff` CLI, a fixtures dataset, and [POLICY-REVIEW.md](docs/POLICY-REVIEW.md).
- C# `PolicyGate` is formalized as authoritative; symbolic metadata supports review.

### Area 3 — Real LLM proposer, hardened
- `LlmIntentProposer` rejects **overbroad** plans whole (cap on actions) and **ambiguous** (no-kind)
  actions, both fail-closed. Emitted nodes carry **model provenance** (`llm:{provider}:{model}`).
- Already proven: malformed output → nothing; hallucinated kinds dropped; unsafe send still gated.
  The model never becomes authority.

### Area 4 — Integrations hardened for realistic use
- MCP stdio: per-response **read timeout** + **8 MiB line cap** (hung/runaway server can't block or
  OOM the runtime).
- `RetryingMcpClient`: transient retry with exponential backoff; fatal errors never retried. Retries
  read-only `ListTools` by default; `CallTool` is **not** retried unless `retryToolCalls` is set
  (a mid-call timeout must never re-issue a non-idempotent send/write/delete).
- OAuth: token carries the **granted scope**; a downgraded grant is refused fail-closed.
- `McpProxy.GateAndForward` reports **progress** for a control room / CLI.

### Area 6 — Control Room → operator workflow
- New `RunExplain` ("why blocked / what would approval do") and Web endpoints: `/api/runs` history,
  run detail, signed-artifact viewer, integrity `verify`, `replay` diff, and `explain`.
- New **Operations** tab in the SPA. Runs are persisted as signed, replayable bundles.

### Area 7 — Persistence & audit operations
- `RotatingAuditKeyProvider` + `AuditSigner.Verify` (resolve-by-KeyId, fail-closed on unknown).
- `FileRunArtifactStore` gains `ListSummaries`, `Archive`, `Prune` (retention).
- `verify-run` CLI subcommand; [AUDIT-OPERATIONS.md](docs/AUDIT-OPERATIONS.md) (key mgmt, rotation,
  retention, tamper-verification).

### Area 8 — Public narrative & release posture
- [MATURITY.md](docs/MATURITY.md) — canonical proven / experimental / future statement.
- This changelog; README refreshed to v1.7.

### PR #5 review hardening
- **Audit key rotation now covers the whole bundle.** `TraceBundle` records the `KeyId` that produced
  its `BundleSignature`; `VerifySignature`, `FileRunArtifactStore.VerifyArtifacts`, and
  `RunReplay.Reproduce` gained `IAuditKeyProvider` overloads that resolve **that recorded key** (not
  just the current one), so a run persisted before a rotation still verifies and replays byte-for-byte.
  CLI/Control Room build the provider via `AuditKeyProviders.FromEnvironment()` (current key +
  `INTENTMESH_AUDIT_PRIOR_KEYS`). Regression-tested.
- **Retry is idempotency-safe** (see Area 4) — `CallTool` no longer retried by default.
- **Stdio line cap enforced during read.** `McpStdioClient` reads in bounded chunks and checks
  `MaxLineChars` as it accumulates (≈ bytes for JSON framing), so a server streaming an unbounded line
  is cut off instead of being buffered whole; framing across messages is preserved.

## v1.6.0 — One externally-credible path, end to end
Real LLM proposer (`LlmIntentProposer` + `AnthropicLlmClient`), file-based run/audit persistence +
replay, live benchmark harness + [comparison report](docs/BENCHMARK-REPORT.md), E2E demo. 138/138 tests.

## v1.5.0 — Kernel hardening
Env/KMS audit key (`IAuditKeyProvider`), fail-closed parsing, symlink/UNC path safety, SSRF-guarded
HTTP transport, provable consent, IntentBench-Red adversarial suite.

## v1.4.0 — Real integrations
MCP stdio + Streamable HTTP/SSE behind `IMcpClient`, OpenAPI import (JSON+YAML, `$ref`), SMTP +
OAuth 2.0 device flow.

## v1.0.0 — Framework seams
Swappable proposer (`IIntentProposer`), capability scoping, tamper-evident signed audit logs.

## v0.1–v0.4 — Prototype
Personal-agent Control Room, confirmation flow, audit export, skill lifecycle, developer-agent and
data-agent demos.
