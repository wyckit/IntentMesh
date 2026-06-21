# IntentMesh — maturity & claims discipline

The single source of truth for **what is production-ready, what is experimental, and what is future
work.** Every "proven" claim below is backed by a test that would fail if the claim stopped being
true (`dotnet test IntentMesh.slnx` — **218 passing, 3 env-gated skipped**). Nothing here is aspirational unless it says so.

> IntentMesh is a **research prototype with a production-shaped core**: the security kernel and its
> guarantees are proven and stable; the *operational backends* around it (KMS, DB persistence,
> multi-tenancy) are seams with reference implementations, not hardened services.

## ✅ Proven — production-ready core (test-backed)

| Capability | Evidence |
|---|---|
| Fail-closed policy gate is the only authority | FrameworkTests, PipelineTests — a blocked node never executes |
| Translation-Drift bound (only registered kinds emit) | LlmProposerTests, SdkTests — hallucinated/unregistered kinds dropped |
| Zero-trust: retrieved content has no authority | PipelineTests — injected side-effect blocked, demo 3 |
| Real LLM proposer, fail-closed | LlmProposerTests — malformed/ambiguous/overbroad/unsafe rejected; provenance stamped; model never authority |
| Signed audit + tamper-evidence (SHA-256 chain + HMAC) | AuditOperationsTests, PersistenceTests |
| Deterministic replay (signature + byte-for-byte re-run) | PersistenceTests, SdkTests |
| Key rotation (resolve-by-KeyId, fail-closed on unknown) | AuditOperationsTests |
| Run persistence + history + retention (file store) | PersistenceTests, AuditOperationsTests |
| Hardened integrations: stdio timeout/size-cap, transient retry, OAuth token-scope, SSRF-guarded HTTP | IntegrationTests |
| MCP proxy gates intent before forwarding (stdio + HTTP/SSE) | IntegrationTests — blocked call never reaches the server |
| OpenAPI import (JSON+YAML, `$ref`, semantic inference) | IntegrationTests |
| Operator workflow: history, approval queue, replay diff, artifact viewer, why-blocked | ExplainTests + Web endpoints, smoke-tested |
| Multi-tenant authz: principal/tenant/role identity, tenant-isolated run store, server-issued approval challenges | AuthTests, WebAuthzTests — cross-tenant run is 404, viewer can't run, only a server-minted challenge approves |
| Stable SDK surface + minimal host template | SdkTests; `templates/IntentMesh.Host.Template` builds **and runs** |

## 🧪 Experimental — works, reference-grade, not hardened

| Capability | Status |
|---|---|
| `AnthropicLlmClient` real LLM path | Works against the live API (env-gated test); not load-tested or cost-managed |
| IntentBench (25 scenarios) | **Architecture demonstration**, not a product benchmark: the IntentMesh column is measured (real pipeline), the vanilla/mcp-gated baselines are deterministic architecture-class *models* (not executed agents) and the criteria are coarse — see [BENCHMARK-REPORT.md](BENCHMARK-REPORT.md). `intentbench --live` runs the proposal layer against a real model. |
| SMTP + OAuth 2.0 device flow | Real transports; need your credentials and a consent screen |
| Control Room SPA | Useful for governance/debugging; dependency-free demo UI. The `/api` surface enforces a real **multi-tenant authz boundary** (see the Proven row above): built-in signed session tokens (`POST /api/auth/token` against a principal store) **or** a trusted reverse-proxy/OIDC header contract (`INTENTMESH_TRUSTED_PROXY=1` + `X-Proxy-Secret`); per-tenant run isolation; role gating (viewer/operator/approver); and **server-issued approval challenges** (caller-asserted approvals are ignored). It **refuses to start in Production** without an auth boundary or with the demo audit key. *Reference-grade, not yet hardened:* no rate limiting/quotas, no SSO/SCIM provisioning, and the principal store is a static JSON file (rotate keys manually). |
| Policy authoring | C# `PolicyGate` is authoritative; symbolic metadata + fixtures/diff support review (no declarative DSL yet) |

## 🔭 Future — named seams, not built (not faked)

- **KMS/HSM key-management backend** behind the existing `IAuditKeyProvider` seam (interface + rotation
  shipped; production keys are currently raw env bytes held in process memory — a managed-KMS/HSM
  backend that never exposes key material is future).
- **Durable, confidential persistence** behind `IRunArtifactStore` (the shipped file store writes
  atomically + tamper-evident and is now **partitioned per tenant** at `{runsDir}/t/{tenant}`, but is
  still **cleartext with no encryption-at-rest, WORM/immutability, or backup/restore**; a DB/blob/WORM
  backend is future). Put the runs dir on an encrypted volume meanwhile — see [DEPLOYMENT.md](DEPLOYMENT.md).
- **Declarative policy DSL** — see [POLICY-AUTHORING.md](POLICY-AUTHORING.md) (C# authoritative today).
- **Live RSRM hot-load** of the `im-*` bundle.
- **Identity-provider provisioning** (SSO/SCIM) and **rate limiting / quotas** on the Control Room API.
  The authz boundary is built (principal/tenant/role, server-issued approvals — see Proven); what remains
  future is bulk provisioning, a managed principal store, and per-tenant rate limits.
- **Fuzz / mutation testing + enforced coverage thresholds** — the suite is example-based today.
- **Live-LLM CI gate** — the real-Anthropic test runs in CI only when the `ANTHROPIC_API_KEY` secret is
  configured (it skips otherwise). The real filesystem-MCP and stdio-MCP E2E paths now DO run in CI.

## How to read a claim

If a row is under **Proven**, find its test and run it. If it's under **Experimental** or
**Future**, treat it as exactly that — a working reference or an unbuilt seam, never a guarantee.
This separation is the point: a verified-intent runtime that overclaims is just another agent.
