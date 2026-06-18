# IntentMesh — 90-Day Execution Roadmap

> From a tested v1.0 prototype to a visual, benchmarked, reproducible proof of the **Verified Intent
> Runtime** category. Six fortnights. See [GOAL-verified-intent-runtime.md](GOAL-verified-intent-runtime.md)
> for the why and [TASKLIST-NEXT.md](TASKLIST-NEXT.md) for the task breakdown.

The biggest product decision: **do not go broad.** Make one narrow thing perfect — *a safe agent
control room that blocks indirect prompt injection while still completing the user's real task.*

---

## Days 1–15 — Public clarity
*Goal: make the repo instantly understandable.*

- README above-the-fold: one-line thesis · demo GIF · normal-vs-IntentMesh · one-command demo ·
  blocked-injection result · why it matters.
- Repo metadata: description "Verified Intent Runtime for AI Agents"; topics `ai-safety, agents,
  mcp, prompt-injection, tool-use, agent-runtime, csharp, dotnet, audit, policy-engine`; license
  clarity; **first release tag (v1.0.0)**.
- Architecture diagram + threat-model doc front-and-center.
- A dedicated "Normal agent vs IntentMesh" page.
- Make `--demo 3` (the injection defense) the canonical front door.

## Days 16–30 — Runtime hardening  *(done)*
*Goal: make the core feel real.*

- **[done]** Versioned schemas for all five artifacts under `schema/` (`intent.graph`,
  `policy.decisions`, `execution.trace`, `verification.report`, `audit.signed`, + `trace.bundle`).
- **[done]** Signed trace bundle (`TraceBundle`) export from CLI (`intentmesh export … --split`)
  and the Control Room (`⬇ Bundle`); HMAC over all five artifacts.
- **[done]** Deterministic replay: `intentmesh replay <bundle>` reloads, re-runs, and asserts the
  bundle is byte-identical + signature valid.
- **[done]** Proposer/Verifier separation enforced: the verifier never receives the prompt; new
  `pc-recipient-contract-match` catches recipient substitution from contract + output alone.
- **[done]** Test suite grown to **57** (was 38).

## Days 31–45 — Control Room v1  *(done)*
*Goal: make the visual product.*

- **[done]** Interactive mesh viewer with a node-detail slide-over (identity, typed fields, policy
  decision, execution, verification); the injected node pulses red with a `QUARANTINED` badge.
- **[done]** Audit timeline (phase-colored rail) + polished policy/verification panels.
- **[done]** Labeled one-click scenario selector (attack vector under each).
- **[done]** Animated normal-vs-IntentMesh comparison with a Replay button; signed-bundle download.

## Days 46–60 — Benchmark v1 (IntentBench)  *(done)*
*Goal: prove the category.*

- **[done]** `IntentMesh.Bench` (`intentbench`): **25 scenarios** across five vectors (email-exfil,
  recipient-substitution, file-injection, dev-shell, data-destructive).
- **[done]** Baseline models: a vanilla agent + an MCP-gated agent, compared on injection-blocked /
  legit-completed / audit-produced / postcondition-verified.
- **[done]** Reproducible runner + markdown report (`bench/REPORT.md`) + public scoreboard
  (`bench/scoreboard.html`). Result: **IntentMesh 25/25**; vanilla blocks 0; MCP-gated blocks only
  the 5 raw-shell cases.

## Days 61–75 — Integration layer  *(done)*
*Goal: become useful beyond the demo.*

- **[done]** Runtime SDK surface (`IntentMeshSdk`): propose → run (compile + policy + execute +
  verify) → bundle/sign; `WithProposer` wraps an existing/LLM agent (proven still gated).
- **[done]** MCP adapter/proxy prototype (`McpProxy`): gates intent before forwarding a tool call;
  real stdio/SSE transport stubbed + documented.
- **[done]** OpenAPI/tool-schema import (`OpenApiImporter`): schema → typed-action-contract descriptor.
- **[done]** Real-OAuth adapter example (`GmailSendAdapter`) behind the `email` capability grant;
  real token flow stubbed. See `docs/INTEGRATIONS.md` for exactly what is stubbed.

## Days 76–90 — Launch package  *(done)*
*Goal: make it shareable.*

- **[done]** Manifesto "The Case for Verified Intent" (`docs/launch/MANIFESTO.md`); launch article +
  90-second video script (`docs/launch/`).
- **[done]** Landing page (`docs/index.html`); architecture whitepaper (`docs/WHITEPAPER.md`);
  "build your first adapter" guide (`docs/ADAPTER-GUIDE.md`).
- **[done]** Per-phase GitHub releases (v1.0.0 → v1.x); public IntentBench scoreboard.
- Video *render* needs a screen recorder (not available here) — the `.tape` script + the written
  script are ready.

---

## Sequencing notes

- **The Control Room wins the category; the CLI is the developer engine.** Don't delay the Control
  Room — a node graph turning red under attack communicates a structural vulnerability that a CLI
  text dump cannot.
- **Personal agent first.** The developer agent wins GitHub stars, but indirect injection via an
  attached file/email is understood by everyone — that is the universal wedge.
- The prototype already covers a lot (5 demos, capability scoping, signed audit, swappable
  proposer). Much of Days 16–45 is *hardening + exposing* what exists, not building from zero.
