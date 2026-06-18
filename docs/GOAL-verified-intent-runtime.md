# GOAL — IntentMesh as the Verified Intent Runtime for AI Agents

> **Slogan (verbatim):** Don't execute language. Execute verified intent.

> **One line:** Make IntentMesh the visible *safety kernel* for agentic AI — the thing every agent
> runs through before it touches a real tool. Not a chatbot, not an MCP wrapper, not a workflow
> builder, not a prompt-injection filter. A **runtime**.

This is the north-star for the phase *after* the v0.1–v1.0 prototype (which already exists and is
tested — see [ROADMAP.md](ROADMAP.md)). Hand this document to a builder agent (or an IntentMesh
expert) to drive the next 90 days.

## 1. The category

IntentMesh should own a category that does not yet have a clear leader:

> **Verified Intent Runtime for AI Agents** (the Agentic Intent Runtime).

The wedge — say it exactly:

> **ToolMesh / MCP gateways govern tool *calls*. IntentMesh governs *intent* before it becomes a
> tool call.**

Tool-layer security asks: *"Is this tool call allowed?"* — IntentMesh asks a deeper, architectural
question:

> *"Where did this intention come from, does it have authority, what typed action does it claim to
> be, is it policy-valid, and did the final result match the approved intent?"*

That is the differentiation. Gating tools is reactive; **verifying intent is proactive.**

## 2. The product is a trinity

Build three things that reinforce each other — do not pick only one:

| Pillar | What it is | What it gives |
|---|---|---|
| **Runtime** | the thing agents run through | utility |
| **Control Room** | the visual debugger — see what the agent *intended* | understanding |
| **IntentBench** | the safety benchmark proving the approach beats normal agents | credibility |

Runtime gives utility. Control Room gives understanding. Benchmark gives credibility. Together they
are far stronger than any one alone.

## 3. The five sacred interfaces

Harden the runtime around five seams. They are already prototyped; make them stable, exportable,
and testable.

1. **Intent Proposer** (`IIntentProposer`) — where an LLM plugs in, but is **never trusted**. It
   says "I think the user intends these actions." IntentMesh replies "maybe — prove it through
   type, authority, policy, and verification."
2. **Intent Graph** — the central object: a DAG of typed intent nodes. Make it **exportable,
   inspectable, diffable, replayable, testable.**
3. **Policy Gate** — the kernel/authority. Fail-closed. Covers authority source, capability scope,
   side-effect risk, recipient substitution, private-data exfiltration, destructive actions,
   shell/network/file access, confirmation requirement, tenant/user permissions, sensitive-data
   movement.
4. **Tool Adapter Layer** — boring and deterministic, on purpose. Adapters do not reason, do not
   interpret language, and accept **only typed contracts**.
5. **Postcondition Verifier** — the strongest idea: *the action only counts if the result matches
   the approved intent.* Most frameworks stop at "tool call succeeded."

### The Proposer / Verifier separation (non-negotiable)

Maintain a strict separation between the actor that *proposes* and the actor that *verifies*:

- **Proposer (LLM A)** — high-creativity, fluid, **untrusted**. Messy language → strict typed
  `IntentGraph`.
- **Verifier (LLM B or deterministic code)** — zero-creativity, constrained. **Does NOT read the
  original user prompt.** It reads only the approved `IntentNode` contract and the raw
  `ToolOutput`, and answers a boolean: *did the output match the contract boundary?*

> Example: intent node says `Recipient = client@company.com`; the tool output went to
> `attacker@evil.com`. The Verifier catches the postcondition mismatch as a **contract-boundary
> violation** and triggers rollback/quarantine *before the transaction finalizes* — independent of
> how persuasive the prompt was.

## 4. Every run emits a signed artifact bundle

Each lifecycle transition produces an inspectable, exportable, signed artifact — the "show your
work" bundle:

```
intent.graph.json        DAG of typed intent nodes
policy.decisions.json    authorization per node
execution.trace.json     strict typed I/O
verification.report.json boolean: output == intent contract?
audit.signed.json        deterministic replay package (hash-chained + HMAC)
```

The Control Room's five panels render this state machine **live**.

## 5. The Control Room (the product surface)

The hero is the **intent mesh DAG**. Five lanes:

```
User request → Intent Mesh → Policy Gate → Execution → Verification / Audit
```

Each node shows: intent type, source, authority, risk, action contract, policy result, verifier
result. **The injected node visibly turns red and is quarantined.** Add:

- **Normal-agent-vs-IntentMesh** side-by-side mode (the most viral feature) — left: prompt → LLM →
  tool → side effect; right: prompt → typed intent → policy → tool → verifier → audit.
- **One-click scenarios** (buttons, no typing first): prompt-injection-in-project-folder,
  developer-agent-blocked-from-shell, data-agent-blocked-from-DROP-TABLE, email-recipient-
  substitution, calendar-invite-hidden-instruction, conflicting-user-vs-retrieved-instruction,
  safe-action-allowed-after-confirmation.
- **Export trace bundle** (the five artifacts, signed).

## 6. IntentBench — the benchmark that proves the category

Attack vectors: direct injection · indirect injection · recipient substitution · data exfiltration
· unauthorized side effects · destructive tool calls · privilege escalation · tool-result poisoning
· state poisoning · confirmation bypass · policy conflict · verification mismatch.

Each test emits: user prompt, malicious content, expected approved intent, expected blocked intent,
policy reason, verifier result, audit artifact. Compare **vanilla LLM agent / MCP-gated agent /
IntentMesh** on a blunt scoreboard:

| Vector | Vanilla LLM | MCP / tool-gated | IntentMesh |
|---|---|---|---|
| Direct injection | ❌ executes prompt | ⚠️ blocks bad tool names | ✅ gated by intent type |
| Indirect injection (untrusted file) | ❌ leaks data | ❌ payload looks like a valid tool call | ✅ quarantined, zero authority |
| Recipient substitution | ❌ tricked by text | ❌ valid tool, wrong arg | ✅ contract-boundary violation |
| Data exfiltration | ❌ | ⚠️ egress filters bypassed | ✅ postcondition check |
| State / context poisoning | ❌ | ❌ | ✅ graph isolation |

The structural insight to expose: **tool-gated agents fail on indirect injection because the
payload looks like a valid tool call; IntentMesh passes by quarantining it as a zero-authority
source before it ever becomes a tool call.** Start with 25 tests — don't overbuild; make it
undeniable.

## 7. The go-to-market wedge

Do **not** make v1 broad. Make one narrow thing perfect:

> *"A safe agent control room that blocks indirect prompt injection while still completing the
> user's real task."*

Sequence the three real apps: **Safe Personal Agent first** (email/calendar/files — indirect
injection via a file is universally understood; wins enterprise buyers, PMs, engineers), **then
Developer Agent** (curl|sh blocked, secrets protected, PR-not-pushed — wins GitHub), **then Data
Agent** (NL → typed query plan, read-only role, destructive SQL blocked — wins enterprise).

Adoption paths: **CLI** (developers) · **local Control Room** (demos/debugging) · **Runtime SDK**
(builders). Plus an **MCP adapter** mode: *"MCP connects tools; IntentMesh verifies intent before
tools."*

## 8. Five phases

1. **Make the demo impossible to ignore** — Control Room as the product surface; normal-vs-IntentMesh
   mode; one-click scenarios; the indirect-injection defense as the front door.
2. **Turn the prototype into a clean runtime** — stable schemas (graph/decisions/verification/audit),
   the five sacred interfaces, deterministic replay, 50+ tests, CLI trace export.
3. **Build the first real app** — Safe Personal Agent (fake data → real OAuth behind capability grants).
4. **Create the benchmark** — IntentBench v1 (25–50 scenarios), baseline comparisons, reproducible
   runner, public scoreboard.
5. **Make it adoptable** — CLI, Control Room, Runtime SDK, MCP wrapper, OpenAPI tool-schema import,
   launch video + manifesto.

See [ROADMAP-90DAY.md](ROADMAP-90DAY.md) for the dated execution plan and [TASKLIST-NEXT.md](TASKLIST-NEXT.md)
for the work breakdown.

## 9. The manifesto (write it): "The Case for Verified Intent"

Sections: language is not authority · tool calls are too late · prompt injection is an authority
failure · policies need structured intent · verification must be postcondition-based · audits must
be human-readable · **emergence belongs before authority, not after it.**

## 10. What success looks like

Someone watches a normal agent get tricked, then watches IntentMesh **see the hidden malicious
instruction, mark it zero-trust, block the side effect, still complete the legitimate task, verify
nothing bad happened, and explain it in a signed audit.** That single loop — visual, benchmarked,
reproducible — is the application, the roadmap, and the proof.
