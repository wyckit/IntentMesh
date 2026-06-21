# Changelog

All notable changes to IntentMesh. Claims are test-backed; see [docs/MATURITY.md](docs/MATURITY.md)
for the production-ready / experimental / future breakdown.

## v1.10.1 — Authz hardening (traversal-safe ids, body-cap)

A follow-up security pass on the v1.10.0 authz surface. **232 passing + 3 env-gated skipped.**

- **Traversal-safe tenant/principal ids** — `AuthIds.IsValid` now rejects ids that start with `.` or `-`
  or contain no alphanumeric, so `.`, `..`, dotfiles, and option-like names are refused even though their
  characters are in the allowed set. Closes a path-traversal / tenant-isolation-bypass risk where a
  `..`-shaped tenant could resolve outside the per-tenant runs root.
- **Tenant-path containment** — the per-tenant store factory re-validates the tenant id and verifies the
  resolved directory stays under the runs root before constructing the store (defense-in-depth).
- **Request-body cap holds for chunked bodies** — the `/api` size guard no longer relies only on a
  declared `Content-Length`; it also sets the Kestrel max-request-body limit so a chunked or
  missing-length body is rejected while binding (an omitted/spoofed `Content-Length` no longer bypasses
  the 256 KB cap).

## v1.10.0 — Real multi-tenant authorization

Builds out the authz boundary that the fourth review flagged as a not-yet-built future seam — it is now
a proven, test-backed feature. **218 passing + 3 env-gated skipped.**

- **Principal / tenant / role identity.** Every `/api` call resolves to an `AuthPrincipal`
  (principal id, tenant, roles: viewer/operator/approver/admin). Two interchangeable modes:
  - *Built-in tokens:* `POST /api/auth/token` exchanges an API key (a file/inline-JSON principal store,
    keys stored only as SHA-256 hashes) for an HMAC-signed session token; `GET /api/auth/whoami` echoes
    the identity.
  - *Trusted proxy:* `INTENTMESH_TRUSTED_PROXY=1` honors `X-Auth-Principal/Tenant/Roles` from an upstream
    IdP/proxy, gated by a shared `X-Proxy-Secret`.
- **Tenant isolation.** Runs are partitioned under `{runsDir}/t/{tenant}`; a run id from another tenant
  returns `404` (existence never leaks). Each run records its owning principal/tenant.
- **Role gating.** Read (history/detail/verify/replay) = any tenant member; run/explain/export =
  `operator`; approval challenges/approve = `approver`.
- **Server-issued approval challenges.** Caller-asserted approvals on `/api/run` are ignored. A gated
  (Confirm) node is approved only by presenting a server-minted challenge bound to `{runId, nodeId,
  tenant}` (`POST /api/runs/{id}/challenges` → `POST /api/runs/{id}/approve`); the approved execution is
  persisted as a new content-addressed run.
- **Fail-closed in Production.** The host refuses to start without an auth boundary, and token mode
  requires a dedicated `INTENTMESH_AUTH_KEY` (≥128-bit) separate from the audit key.
- **Zero new dependencies** — signing is HMAC-SHA256 reusing the 128-bit key floor; 18 new tests
  (`AuthTests`, `WebAuthzTests`). See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for configuration.

## v1.9.2 — Fourth review hardening pass (key floor, draft gate, capabilities, supply chain, deployment)

Closes a fourth external review (2 Blockers + 7 High). **200 passing + 3 env-gated skipped.**

- **128-bit key floor on every signing path (H):** `RawKeyProvider`, `FixedKeyProvider`, and the
  `byte[]` `SignString` path now go through `AuditSigner.RequireStrongKey` and reject keys < 16 bytes —
  the raw-key APIs can no longer bypass the floor.
- **Draft recipient checked at the gate, pre-creation (H):** a draft to a recipient the user did not
  name now returns `Confirm` *before* the draft exists (`pol-recipient-not-requested`), and the adapter
  halts instead of creating it — no longer postcondition-only.
- **Imported OpenAPI capabilities enforced at runtime (H):** `SymbolicBundle` now seeds capabilities
  from `ActionContract.Capability`, so imported tools are gated by their declared capability after
  registration (ToolAdapter mappings still win on conflict).
- **Release supply-chain hardening (H):** Actions SHA-pinned, workflow permissions minimized to
  least-privilege, packages shipped with `SHA256SUMS` and a build-provenance attestation.
- **Production deployment model (Blocker):** `/healthz` + `/readyz` endpoints, a non-root multi-stage
  Dockerfile with HEALTHCHECK, `AllowedHosts` tightened off `*`, and `docs/DEPLOYMENT.md` (TLS/proxy
  contract, required env vars, host hardening).
- **Honest scope for unbuilt backends (Blocker + H):** authz/tenant boundary, durable encrypted/WORM
  storage, and managed KMS/HSM are documented in [docs/MATURITY.md](docs/MATURITY.md) as named-but-unbuilt
  future seams (not fabricated) — with interim guidance to run the runs dir on an encrypted volume.

## v1.9.1 — Third review hardening pass (MIT relicense + fixes)

Closes a third external review (1 Critical + 8 High). **198 passing + 3 env-gated skipped.**

- **Relicensed to MIT** (Critical): `LICENSE.txt` is now MIT and packages carry the `MIT` SPDX
  expression — the "not licensed for production" block is removed; README reconciled.
- **Replay verifies before executing (H1):** `RunReplay.Reproduce` no longer re-runs a bundle whose
  signature fails — a tampered bundle can't become an unsigned execution path.
- **runId bound to identity (H2):** `VerifyArtifacts` requires `runId == RunIdOf(bundle)`; a signed run
  copied under another id is not reported intact.
- **Query policy allow-list (H3):** only known read ops (case-insensitive) are read-only; lowercase
  variants and unknown write ops are blocked under a read-only role.
- **npx arg guard (H4):** `ConnectNpx` rejects shell metacharacters (Windows `cmd.exe /c` re-parsing).
- **Always-guarded MCP HTTP (H5):** `Connect` no longer accepts an injectable `HttpClient`; the
  SSRF-guarded client is always used.
- **Atomic persistence + surfaced failures (H7):** artifacts are written atomically (temp + move) and
  `/api/run` sets `X-Persist-Error` instead of silently claiming success.
- **Cross-checkout reproducible builds (H8):** SourceLink + deterministic source paths — verified two
  different-path checkouts produce byte-identical assembly hashes (corrects the v1.9.0 same-dir-only claim).
- **CI runs real security paths (H9):** `INTENTMESH_FS_E2E=1` (real filesystem MCP) + `ANTHROPIC_API_KEY`
  from secrets (live-LLM when configured).
- **Honest Control Room scope (H6):** documented as not-production-authz (loopback/single-token,
  caller-asserted approvals). Web `/api` body capped at 256 KB.

## v1.9.0 — Second security review hardening pass

Closes a second external review (9 High, plus Mediums and release hygiene). **191 passing + 3
env-gated skipped.** Includes a deliberate audit-format change (bundle schema 1.1).

### Signature binding (High)
- **Whole-transcript signing (H-A):** `SignedAudit` verification now binds the FULL `AuditJson`
  (prompt, nodes, policy, execution, verification, summary), not just the audit-event chain.
- **Bundle top-level fields (H-B):** the `TraceBundle` signature now covers `Prompt`, `Approvals`,
  `Summary`, `SchemaVersion`, and `KeyId` too. **Bundle schema → 1.1** (stronger scheme; pre-1.1
  bundles do not verify under it).

### Enforcement (High)
- **MCP forward allow-list (H-C):** the proxy forwards ONLY `Allowed/Executed/Verified`; a `Halted`
  (or any unexpected) status is no longer forwarded.
- **Shell allow-list (H-D):** matches the executable (exact, or entry + space + args) and rejects shell
  metacharacters — `dotnet test; rm -rf /` and `dotnet testxyz` are blocked.
- **Per-file deletion (H-E):** a destructive delete requires explicit per-file approval
  (`node#fileRef`); a bare node approval no longer blanket-deletes. SPA renders per-file approve buttons.
- **Validated-path forwarding (H-F):** the MCP proxy forwards the canonical in-root path it validated,
  and scopes no-path filesystem tools to the root (H-I).

### Hardening + release (High)
- **SSRF guard scope (H-I):** documented that the redirect/DNS-rebinding guard applies to the default
  MCP HTTP client; a caller-supplied client is the caller's responsibility.
- **Control Room (H-G):** the SPA sends `X-Api-Token` (from localStorage) so token auth doesn't break
  it; the web surface is now test-gated via `WebApplicationFactory` (loopback, token enforcement, run
  persistence).
- **Reproducible builds (H-H):** `global.json` pins SDK 10.0.201 (`rollForward: disable`); CI uses
  ubuntu-24.04 + exact SDK; `Deterministic` + `ContinuousIntegrationBuild` produce byte-identical
  assemblies (verified).

### Medium + docs
- Control Room `/api` rejects bodies over 256 KB (basic DoS guard). MATURITY future section names the
  remaining gaps honestly (rate limiting, fuzz/mutation/coverage, real-LLM/FS-MCP CI gates). README
  license reconciled with `LICENSE.txt` (research/evaluation only).

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
