# IntentMesh — Next-Phase Task List (Verified Intent Runtime)

> **Historical task breakdown.** The Phase 5 integration items (MCP transport, OpenAPI import, OAuth)
> are **shipped** as of v1.4–v1.5 (see [INTEGRATIONS.md](INTEGRATIONS.md)); the current forward plan
> is [V1.6-SCOPE.md](V1.6-SCOPE.md).

> Work breakdown for the 90-day push. Grouped by phase; each item is sized to be one task. See
> [GOAL-verified-intent-runtime.md](GOAL-verified-intent-runtime.md) and
> [ROADMAP-90DAY.md](ROADMAP-90DAY.md). Expert owner shown in `[brackets]` — dispatch in parallel.

Experts (engram leaf namespaces under `verified_intent_runtime`):
`runtime` = intentmesh_runtime_architect · `control` = intentmesh_controlroom_designer ·
`bench` = intentmesh_benchmark_engineer · `integ` = intentmesh_integration_engineer ·
`launch` = intentmesh_launch_strategist.

## Phase 1 — Public clarity (Days 1–15)

- [ ] **P1.1 README above-the-fold** `[launch]` — thesis line, GIF, normal-vs-IntentMesh, one-command
  demo, blocked-injection result, why it matters.
- [ ] **P1.2 Repo metadata + release** `[launch]` — GitHub description "Verified Intent Runtime for AI
  Agents"; topics (ai-safety, agents, mcp, prompt-injection, tool-use, agent-runtime, csharp, dotnet,
  audit, policy-engine); tag **v1.0.0** release with notes.
- [ ] **P1.3 Demo GIF + architecture diagram** `[launch/control]` — record `--demo 3` and the Control
  Room; SVG architecture/pipeline diagram.
- [ ] **P1.4 "Normal agent vs IntentMesh" page** `[launch/control]` — standalone, animated, blunt.
- [ ] **P1.5 CI + badge** `[runtime]` — GitHub Actions `dotnet test` + tlm `verify` on push; green badge.

## Phase 2 — Runtime hardening (Days 16–30)

- [ ] **P2.1 Versioned artifact schemas** `[runtime]` — `intent.graph.json`, `policy.decisions.json`,
  `execution.trace.json`, `verification.report.json`, `audit.signed.json` + JSON Schema files.
- [ ] **P2.2 Signed trace-bundle export** `[runtime]` — emit all five artifacts as one signed bundle
  from CLI + Control Room.
- [ ] **P2.3 Deterministic replay** `[runtime]` — load a bundle, re-run, assert identical decisions; a
  `intentmesh replay <bundle>` command.
- [ ] **P2.4 Enforce Proposer/Verifier separation in code** `[runtime]` — the Verifier must not receive
  the original prompt; only the approved IntentNode contract + raw ToolOutput. Add a recipient-
  substitution postcondition test (intent says X, output went to Y → contract-boundary violation).
- [ ] **P2.5 Grow to 50+ tests + failure-mode coverage** `[runtime]`.

## Phase 3 — Control Room v1 (Days 31–45)

- [ ] **P3.1 Interactive mesh viewer** `[control]` — DAG hero; node detail (type, source, authority,
  risk, contract, policy result, verifier result); injected node turns red + quarantined live.
- [ ] **P3.2 Policy / verification / audit panels** `[control]` — decision panel, verification panel,
  audit timeline.
- [ ] **P3.3 One-click scenario selector** `[control/bench]` — the seven canonical attack buttons.
- [ ] **P3.4 Comparison view v2** `[control]` — animated normal-vs-IntentMesh.
- [ ] **P3.5 Export button** `[control/runtime]` — download the signed trace bundle.

## Phase 4 — IntentBench v1 (Days 46–60)

- [ ] **P4.1 Benchmark schema + runner** `[bench]` — test format (prompt, malicious content, expected
  approved/blocked intent, policy reason, verifier result, audit); reproducible runner.
- [ ] **P4.2 25 seed scenarios** `[bench]` — 5 each: email-exfil, recipient-substitution, file-
  instruction-injection, developer-shell, data-destructive-query.
- [ ] **P4.3 Baseline harness** `[bench/integ]` — vanilla LLM agent + MCP-gated agent baselines to
  compare against.
- [ ] **P4.4 Scoreboard + markdown report generator** `[bench]` — the blunt matrix; public page.

## Phase 5 — Integration layer (Days 61–75)

- [ ] **P5.1 Runtime SDK surface** `[integ]` — `propose → compileGraph → evaluatePolicy →
  executeTypedAction → verify → exportAudit`; wrap-an-existing-agent example.
- [ ] **P5.2 MCP adapter/proxy prototype** `[integ]` — sit in front of MCP tools; "MCP connects tools,
  IntentMesh verifies intent before tools."
- [ ] **P5.3 OpenAPI tool-schema import** `[integ]` — wrap REST/MCP schemas in typed intent contracts.
- [ ] **P5.4 Real-OAuth adapter examples** `[integ/runtime]` — promote fake adapters to clean real
  adapters behind capability grants (opt-in, approval-gated).

## Phase 6 — Launch package (Days 76–90)

- [ ] **P6.1 Manifesto "The Case for Verified Intent"** `[launch]`.
- [ ] **P6.2 Launch video** `[launch]` — the attack → intent → loss of authority → block → legit
  success → verify → audit.
- [ ] **P6.3 Landing page** `[launch]`.
- [ ] **P6.4 Architecture whitepaper** `[runtime/launch]`.
- [ ] **P6.5 "Build your first IntentMesh adapter" guide** `[integ]`.

## Acceptance for the phase

People can: watch the attack, see the intent graph, see the malicious node lose authority, see the
policy block it, see the legitimate task succeed, see the verifier prove it, and download a signed
audit that explains it — and a public benchmark shows IntentMesh passing where vanilla and
tool-gated agents fail.
