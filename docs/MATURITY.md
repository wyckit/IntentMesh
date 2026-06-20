# IntentMesh — maturity & claims discipline

The single source of truth for **what is production-ready, what is experimental, and what is future
work.** Every "proven" claim below is backed by a test that would fail if the claim stopped being
true (`dotnet test IntentMesh.slnx` — **183 passing, 3 env-gated skipped**). Nothing here is aspirational unless it says so.

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
| Stable SDK surface + minimal host template | SdkTests; `templates/IntentMesh.Host.Template` builds **and runs** |

## 🧪 Experimental — works, reference-grade, not hardened

| Capability | Status |
|---|---|
| `AnthropicLlmClient` real LLM path | Works against the live API (env-gated test); not load-tested or cost-managed |
| IntentBench (25 scenarios) | **Architecture demonstration**, not a product benchmark: the IntentMesh column is measured (real pipeline), the vanilla/mcp-gated baselines are deterministic architecture-class *models* (not executed agents) and the criteria are coarse — see [BENCHMARK-REPORT.md](BENCHMARK-REPORT.md). `intentbench --live` runs the proposal layer against a real model. |
| SMTP + OAuth 2.0 device flow | Real transports; need your credentials and a consent screen |
| Control Room SPA | Useful for governance/debugging; dependency-free demo UI. The `/api` surface is **enforced local-only** — a non-loopback caller is refused unless `INTENTMESH_WEB_TOKEN` is set, in which case every API call must present it (`X-Api-Token` / `Authorization: Bearer`). It also **refuses to start in Production with the demo audit key** (`INTENTMESH_AUDIT_KEY`, or `INTENTMESH_ALLOW_INSECURE_KEY=1` to opt out). Full auth, rate-limiting, and multi-tenant isolation remain future. |
| Policy authoring | C# `PolicyGate` is authoritative; symbolic metadata + fixtures/diff support review (no declarative DSL yet) |

## 🔭 Future — named seams, not built (not faked)

- **KMS/HSM key-management backend** behind the existing `IAuditKeyProvider` seam (interface + rotation shipped; cloud backend future).
- **Durable persistence backends** behind `IRunArtifactStore` (file store shipped; DB/blob future).
- **Declarative policy DSL** — see [POLICY-AUTHORING.md](POLICY-AUTHORING.md) (C# authoritative today).
- **Live RSRM hot-load** of the `im-*` bundle.
- **Multi-tenant isolation / authn-z** for the Control Room and run store.

## How to read a claim

If a row is under **Proven**, find its test and run it. If it's under **Experimental** or
**Future**, treat it as exactly that — a working reference or an unbuilt seam, never a guarantee.
This separation is the point: a verified-intent runtime that overclaims is just another agent.
