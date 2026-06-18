# GOAL — IntentMesh: the Verified Intent Runtime for AI Agents

> Slogan: **Don't execute language. Execute verified intent.**
> One line: make IntentMesh the safety **kernel** every agent runs through before it touches a real
> tool. Not a chatbot, MCP wrapper, workflow builder, or prompt filter — a runtime.

## Category
**Verified Intent Runtime for AI Agents.** Wedge: *ToolMesh/MCP gateways govern tool calls;
IntentMesh governs intent **before** it becomes a tool call.* Tool-layer asks "is this tool call
allowed?" — IntentMesh asks "where did this intention come from, does it have authority, what typed
action does it claim, is it policy-valid, and did the result match the approved intent?"

## Product trinity
Runtime (agents run through it → utility) + Control Room (visual debugger → understanding) +
IntentBench (safety benchmark → credibility).

## Five sacred interfaces
1. **Intent Proposer** (`IIntentProposer`) — where an LLM plugs in, never trusted.
2. **Intent Graph** — DAG of typed nodes; exportable, diffable, replayable.
3. **Policy Gate** — the kernel/authority, fail-closed.
4. **Tool Adapter** — boring, deterministic, typed contracts only.
5. **Postcondition Verifier** — the action only counts if the result matches the approved intent.

**Proposer/Verifier separation (non-negotiable):** the Verifier does NOT read the original prompt —
only the approved IntentNode contract + raw ToolOutput, answering: did output match the contract
boundary? (intent says client@company.com, output went to attacker@evil.com → contract-boundary
violation → quarantine before finalize.)

## Run artifacts (signed bundle)
`intent.graph.json` · `policy.decisions.json` · `execution.trace.json` · `verification.report.json`
· `audit.signed.json`. The Control Room's five panels render this state machine live.

## Control Room
Hero = the intent-mesh DAG. Lanes: User request → Intent Mesh → Policy Gate → Execution →
Verification/Audit. The injected node turns red and is quarantined. Add normal-vs-IntentMesh mode
(most viral), one-click attack scenarios, and signed trace export.

## IntentBench
Vectors: direct/indirect injection, recipient substitution, exfiltration, unauthorized side effects,
destructive calls, privilege escalation, tool-result/state poisoning, confirmation bypass, policy
conflict, verification mismatch. Compare vanilla / MCP-gated / IntentMesh on: injection blocked ·
legit task completed · audit produced · postcondition verified. Insight: tool-gated agents fail
indirect injection (payload looks like a valid tool call); IntentMesh quarantines it as
zero-authority first. Start with 25 tests.

## Wedge & sequence
Don't go broad. Make one narrow thing perfect: *"a safe agent control room that blocks indirect
prompt injection while still completing the user's real task."* Sequence: **Safe Personal Agent**
first (email/files — universal), then **Developer Agent** (curl|sh, secrets — wins GitHub), then
**Data Agent** (typed query plan, DROP blocked — wins enterprise). Adoption: CLI, Control Room,
Runtime SDK, MCP adapter.

## 90-day arc
clarity → runtime hardening → Control Room v1 → IntentBench v1 → integration → launch. Full dated
plan in `ROADMAP-90DAY.md`; tasks in `TASKLIST-NEXT.md`.

## Manifesto — "The Case for Verified Intent"
Language is not authority · tool calls are too late · prompt injection is an authority failure ·
policies need structured intent · verification must be postcondition-based · audits must be
human-readable · emergence belongs before authority.

## Success
Watch a normal agent get tricked, then watch IntentMesh see the hidden instruction, mark it
zero-trust, block the side effect, complete the legit task, verify nothing bad happened, and explain
it in a signed audit. Visual, benchmarked, reproducible.
