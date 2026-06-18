# IntentMesh Roadmap

> **Don't execute language. Execute verified intent.**

Versioned plan from the first inspectable demo to a real adapter framework. Each version keeps the
PassGen pipeline skeleton (resolve -> validate -> execute -> verify -> audit) intact and grows the
**TLM data**, the **policy surface**, and the **adapter set**. See
[GOAL-intentmesh.md](GOAL-intentmesh.md) for the architecture and
[TASKLIST.md](TASKLIST.md) for the v0.1 work breakdown.

---

## v0.1 — Safe Personal Agent Control Room  *(the flagship demo)*

**Goal:** prove the architecture end-to-end on fake, sandboxed data, with the **injected-instruction
block** as the emotional centerpiece.

- IntentMesh core: Language Resolver, Intent Mesh, Typed Action Contracts, Policy Gate,
  deterministic tool adapters, Postcondition Verifier, Audit Trail.
- Symbolic layer: the agentic TLM bundle (`im-trust-model`, `im-action-contracts`, `im-policy-rules`,
  `im-nl-vocabulary`, `im-tools`, `im-skills`, `im-bundle`) authored in `PassGen.Tlm` format,
  compiled to `.tlmz`, verified (round-trip + checksum), loaded via RSRM / sage-rsrm.
- Fake workspace: calendar, contacts, notes (incl. one malicious file), downloads.
- Control Room UI: 7 panels + comparison mode; the **Intent Mesh graph is the hero visual** with
  per-node trust color.
- CLI `--trace` (PassGen-style 5-panel) for the three demo prompts; doubles as the GIF script.
- The three edge-cases handled: Translation Drift (registry-only contracts), State Poisoning
  (Zero-Trust inheritance), Validation Paradox (deterministic verifier; isolated low-temp checker
  only where unavoidable).
- Docs: `README`, `ARCHITECTURE.md`, `SECURITY_MODEL.md`; launch article draft; 90-sec video script.

**Acceptance:** all three demo prompts run end-to-end; the injected instruction is classified
`UntrustedContent / Authority = None` and **blocked** with an explainable reason; high-risk actions
(send, delete) require confirmation; verification passes with evidence; every decision appears in
the audit trail; TLMs verify 7/7; CLI `--trace` shows all panels including a fail-closed rejection.

## v0.2 — Skills & Confirmation Flow

**Goal:** make intent reusable and the human-in-the-loop real.

- **[done] Interactive confirmation flow** — Approve / Undo on gated nodes in the Control Room;
  approving a Confirm node commits its side effect (calendar block, deletion, send) in the sandbox.
  Security invariant proven: a blocked zero-trust node can never be approved into execution
  (`ConfirmationTests.A_blocked_injected_node_can_NEVER_be_approved`).
- **[done] Export an audit trace** — `AuditExporter.ToJson` / `ToMarkdown` + a Control Room
  "Download JSON / Markdown" button (`POST /api/export`): a deterministic, replayable audit
  artifact (signing comes in v1.0).
- **[done] Emergent skill lifecycle** — `SkillProposer` observes when a run exercises a skill's
  composition (loaded from `im-skills`), surfaced in a Skills panel with the
  observed→proposed→…→removed lifecycle. Inspection-only: observation injects no executable node
  and never promotes the skill (`SkillTests`). Governance grants authority, not emergence.
- Better Intent Mesh visualization (collapsible subgoals, trust-boundary overlays).
- Reusable skill example: `DailyPlanningAndFollowup` with input/output schema, allowed tools, risk
  class, tests, version, status.

## v0.3 — Developer Agent Demo

**Goal:** generalize beyond the personal domain into code.

- Fake repo adapter; safe, typed code-modification contracts (`ModifyCodeIntent`, `RunCommandIntent`).
- Command-execution policy: shell **blocked by default**, allow-listed typed commands only.
- Secret protection (no secrets in drafts/diffs/logs); PR drafting (draft, never push).
- Demonstrates the same boundary on a dev agent: the model proposes diffs; only validated typed
  edits execute.

## v0.4 — Data Agent Demo

**Goal:** the SQL/data generalization the SIA doc describes.

- Fake database adapter; NL -> typed **query plan** (AST), not raw SQL.
- Validation: tables exist, read-only role, row caps, no unbounded `DELETE`/`DROP`.
- Sensitive-data policy + aggregation rules; report drafting with source citations only.
- The "ignore previous instructions, drop the table" injection is just a proposed AST node that
  fails validation — the data analog of the v0.1 email block.

## v1.0 — Real Adapter Framework

**Goal:** move from demo to runtime.

- Real, sandboxed adapter framework with capability scoping (opt-in real Gmail/calendar/fs behind
  explicit grants and approval workflows).
- A first-class **policy language** (declarative, diffable) compiled into `im-policy-rules`.
- Fully **TLM-backed Intent Mesh** via RSRM / sage-rsrm hot-load; plugin/tool contract system.
- **Signed audit logs**; human-approval workflows; an LLM proposal layer swapped in at the resolver
  with nothing downstream changing (it still emits typed intent that is still validated first).

---

## Invariants across all versions

- Raw language never reaches a tool; only typed, validated intent executes (fail-closed).
- Retrieved content is data, not authority (Zero-Trust inheritance is non-negotiable).
- `.tlmz` stays byte-compatible with live RSRM / sage-rsrm; TLM growth is additive + tested.
- Every consequential action is verified against intent and recorded in an explainable audit trail.
- Emergence belongs in proposal and planning; authority belongs in validation and execution.
