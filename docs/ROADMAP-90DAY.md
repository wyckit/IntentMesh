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

## Days 16–30 — Runtime hardening
*Goal: make the core feel real.*

- Stable, versioned schemas: `intent.graph.json`, `policy.decisions.json`,
  `verification.report.json`, `audit.signed.json` (+ JSON Schema files).
- Signed trace bundle export (all five artifacts) from CLI and Control Room.
- **Deterministic replay**: load a bundle, re-run, assert identical decisions.
- Failure-mode tests; grow to **50+ unit tests**.
- Enforce the Proposer/Verifier separation in code (Verifier cannot see the original prompt).

## Days 31–45 — Control Room v1
*Goal: make the visual product.*

- Interactive mesh viewer (the hero), policy-decision panel, verification panel, audit timeline.
- Scenario selector (one-click attacks) + export button.
- Normal-vs-IntentMesh comparison view, animated and blunt.
- Polish so the injected node visibly turns red and is quarantined in real time.

## Days 46–60 — Benchmark v1 (IntentBench)
*Goal: prove the category.*

- IntentBench with **25–50 scenarios** across the attack vectors.
- Baseline comparison harness: vanilla LLM agent · MCP-gated agent · IntentMesh.
- Reproducible test runner + markdown report generator.
- Public benchmark page with the scoreboard.

## Days 61–75 — Integration layer
*Goal: become useful beyond the demo.*

- MCP wrapper/proxy prototype (sit in front of MCP tools).
- OpenAPI tool-schema import → typed intent contracts.
- Promote the fake Gmail/Calendar/File adapters to clean real-OAuth adapter examples behind
  capability grants.
- Confirmation-flow polish; local config file; Runtime SDK surface
  (`propose → compileGraph → evaluatePolicy → executeTypedAction → verify → exportAudit`).

## Days 76–90 — Launch package
*Goal: make it shareable.*

- Launch video (the attack → see intent → malicious node loses authority → policy blocks → legit
  task succeeds → verifier proves → signed audit explains).
- Landing page; blog post / manifesto "The Case for Verified Intent".
- Hosted or downloadable demo; GitHub release; architecture whitepaper.
- "Build your first IntentMesh adapter" guide.

---

## Sequencing notes

- **The Control Room wins the category; the CLI is the developer engine.** Don't delay the Control
  Room — a node graph turning red under attack communicates a structural vulnerability that a CLI
  text dump cannot.
- **Personal agent first.** The developer agent wins GitHub stars, but indirect injection via an
  attached file/email is understood by everyone — that is the universal wedge.
- The prototype already covers a lot (5 demos, capability scoping, signed audit, swappable
  proposer). Much of Days 16–45 is *hardening + exposing* what exists, not building from zero.
