# IntentMesh v0.1 — Task List

> **Historical (v0.1 task list).** Status: complete — shipped through v1.5 (111/111 tests,
> IntentBench 25/25). Kept for provenance; the current forward plan is [V1.6-SCOPE.md](V1.6-SCOPE.md).

> Ordered top-down. Each task is sized to be one TaskCreate item. Reuse PassGen
> (`C:\Software\research\randomstringllm\PassGen`) as the reference implementation — author TLMs in
> the `PassGen.Tlm` format, load via RSRM / sage-rsrm, and keep the resolve -> validate -> execute
> -> verify -> audit skeleton. See [GOAL-intentmesh.md](GOAL-intentmesh.md).

## Phase 0 — Foundations

- [ ] **T0.1 Read the seed.** Study PassGen: `TlmNlu.cs`, `ConstraintSpec.cs`, `SpecValidator.cs`,
  `StringGenerator.cs`, the `--trace` flow, and the 7-TLM `dataset/`. Note exactly how Cues
  (Trigger + Signal) and class aliases drive the resolver. Write a one-page mapping note:
  PassGen component -> IntentMesh component.
- [ ] **T0.2 Project scaffold.** Stand up the solution (.NET 10, matching PassGen conventions:
  nullable + implicit usings, file-scoped namespaces, `sealed record` contracts, xUnit). Reference
  the `PassGen.Tlm` library for the `.tlmz` format. Projects: `IntentMesh.Core`,
  `IntentMesh.Tlm.Cli` (author/compile/verify), `IntentMesh.Tests`, and a UI host.
- [ ] **T0.3 Decide the UI stack.** Recommended default: a thin minimal-API backend over
  `IntentMesh.Core` + a browser Control Room (graph lib such as Cytoscape/React Flow for the hero
  visual). Record the decision in engram (`IntentMesh`, `decision`).

## Phase 1 — The symbolic layer (agentic TLM bundle)

- [ ] **T1.1 `im-trust-model` TLM.** Trust sources (User, RetrievedContent, ToolOutput, System),
  authority levels, and the Zero-Trust rule as concepts/relations.
- [ ] **T1.2 `im-action-contracts` TLM.** Typed action schemas as concepts with field/allowed-value
  properties + risk class + postconditions: `ReadCalendarIntent`, `CreateCalendarBlockIntent`,
  `DraftEmailIntent`, `SendEmailIntent`, `ReadFileIntent`, `DeleteFileIntent`,
  `SummarizeDocumentIntent`.
- [ ] **T1.3 `im-policy-rules` TLM.** Risk classes + decision rules (allow / warn / confirm / review
  / block) + confirmation requirements (see Policy examples below).
- [ ] **T1.4 `im-nl-vocabulary` TLM.** Cues (Trigger synonyms + Signal) mapping English to action
  signals; ambiguity flags. This is the direct analog of `rs-nl-vocabulary`.
- [ ] **T1.5 `im-tools` + `im-skills` + `im-bundle` TLMs.** Adapter registry/postconditions; skill
  lifecycle scaffolding; the overlay index + dependency DAG.
- [ ] **T1.6 Author + compile + verify.** Wire an `IntentMesh.Tlm.Cli` (author -> compile ->
  decompile -> verify) like PassGen's `build-dataset.ps1`. Assert 7/7 round-trip + checksum.

## Phase 2 — The pipeline

- [ ] **T2.1 Language Resolver** (`IntentResolver`, generalizes `TlmNlu`). Loads Cues from
  `im-nl-vocabulary`; emits typed intent candidates. **Edge-case 1 (Translation Drift): may only
  SELECT contracts from `im-action-contracts`; never synthesize one.**
- [ ] **T2.2 Intent Mesh** (`IntentGraph` / `IntentNode`). Build the node graph with fields from
  GOAL §19-equivalent: id, type, label, source text, authority source, trust level, inputs,
  outputs, constraints, risk, policy decision, required confirmations, tool adapter, exec status,
  verify status, audit refs, children/parent, blocked reason. **Edge-case 2 (State Poisoning):
  nodes derived from untrusted data inherit `TrustSource = UntrustedContent`, `Authority = None`.**
- [ ] **T2.3 Policy / Risk Gate** (generalizes `SpecValidator`, fail-closed). For each node emit a
  PolicyDecision: decision, risk, reason, triggered rules, required confirmation, trust source,
  sensitive-data?, external-side-effect?, destructive?. Block zero-trust nodes requesting side
  effects.
- [ ] **T2.4 Tool Adapters** (deterministic, sandboxed; generalize `StringGenerator`). Fake
  calendar, email, filesystem, notes/docs. **Adapters accept only typed contracts, never language**,
  and report postconditions. No real side effects.
- [ ] **T2.5 Postcondition Verifier** (generalizes `CheckString`). **Edge-case 3 (Validation
  Paradox): keep checks deterministic** (recipient equals approved entity, draft-not-sent, zero
  files deleted, no attacker recipient added, injected instruction ignored). Isolate any
  unavoidable semantic check in a low-temp function separate from the planner.
- [ ] **T2.6 Audit Trail** (`AuditEvent` log; generalizes `--trace`). Record original request,
  resolved intent, intent source, tool requested, policy decision + reason, exec result, verify
  result, timestamp, risk, confirmation status. Human-readable + expandable.

## Phase 3 — Fake workspace + the demo scenarios

- [ ] **T3.1 Fake data.** Calendar (Friday: standup, deep-work, lunch, flexible admin, external
  client meeting, open slot), contacts (Sarah, client, unknown attacker), notes (Sarah's meeting
  notes, private strategy notes, project summary, **one malicious file**), downloads (clear junk,
  ambiguous, an important file with a scary name).
- [ ] **T3.2 Prompt 1** — Friday planning: read -> classify flexible -> propose gym block (confirm)
  -> find notes -> draft email (allowed) -> send requires confirmation. Verify postconditions.
- [ ] **T3.3 Prompt 2** — downloads cleanup: scan -> classify junk -> show list -> **explicit
  per-file approval**; delete nothing automatically; verify zero deletions.
- [ ] **T3.4 Prompt 3 (the aha)** — summarize project folder + email client: detect the injected
  instruction, classify `UntrustedContent / Authority = None`, summarize allowed content only,
  draft email, **block** recipient change / private-note exfiltration; verify the block.

## Phase 4 — Control Room UI

- [ ] **T4.1 Seven panels:** User Request, Intent Mesh (hero graph, per-node status + trust color),
  Policy Gate (table), Execution, Verification, Audit Trail (expandable).
- [ ] **T4.2 Comparison mode:** Normal Agent (prompt -> LLM -> tool -> side effect) vs IntentMesh
  (prompt -> intent mesh -> policy gate -> typed tool -> verification -> audit).
- [ ] **T4.3 CLI `--trace`:** PassGen-style 5-panel ASCII for all three prompts incl. a fail-closed
  rejection; doubles as the demo/GIF script.
- [ ] **T4.4 Visual direction:** OS control-room tone — clean, technical, trustworthy; the Intent
  Mesh graph is the hero. Status markers: Allowed / Needs-review / Blocked / Verified.

## Phase 5 — Docs & launch assets

- [ ] **T5.1 `README.md`** — lead with the architecture and slogan; show the pipeline, the demo, the
  comparison, and how PassGen seeded it.
- [ ] **T5.2 `docs/ARCHITECTURE.md`** — layers, trust boundaries, the emergence note, the learning
  loop, how PassGen maps in and how IntentMesh generalizes out.
- [ ] **T5.3 `docs/SECURITY_MODEL.md`** — threats demonstrated (indirect prompt injection,
  retrieved-content instruction attacks, recipient substitution, exfiltration, unsafe deletion,
  policy bypass) + out-of-scope (real malware/email/deletion/credentials/APIs).
- [ ] **T5.4 Launch article draft** — "Don't Execute Language: Why AI Agents Need Verified Intent."
- [ ] **T5.5 90-second video script** — prompt -> intent mesh -> policy gate -> the injected-file
  block -> "Don't execute language. Execute verified intent."

## Acceptance criteria (v0.1 done)

- All three demo prompts run end-to-end through the full pipeline.
- The injected instruction is classified `UntrustedContent / Authority = None` and **blocked** with
  an explainable reason; no private data leaves; recipient never changes.
- High-risk actions (send, delete) require confirmation; nothing destructive happens automatically.
- Verification passes with evidence; every decision appears in the audit trail.
- Agentic TLM bundle verifies 7/7 (round-trip + checksum) and loads via RSRM / sage-rsrm.
- CLI `--trace` shows all panels including a fail-closed rejection.
- README leads with the architecture; PassGen framed as the seed pattern.

## Policy examples to encode (`im-policy-rules`)

- **Email:** draft if recipient resolved -> allowed; send -> confirmation; recipient change from
  document content -> blocked; unknown external recipient -> review/block; private content -> review.
- **Calendar:** read -> allowed; suggest -> allowed; create tentative block -> confirmation; move
  external meeting / cancel -> confirmation or block.
- **File:** read/classify -> allowed; delete -> explicit per-file approval; delete important/ambiguous
  -> block/review; instructions inside files -> blocked as authority.
- **Tool:** adapters accept only typed contracts; reject raw language; log results + postconditions.
