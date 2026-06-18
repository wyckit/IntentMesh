# IntentMesh: A Verified Intent Runtime for AI Agents

*Architecture whitepaper · v1 · github.com/wyckit/IntentMesh*

## Abstract

Agentic AI systems increasingly take consequential actions — sending email, modifying code,
querying databases, calling APIs — driven by natural language. The dominant architecture lets
language (from the user, retrieved documents, or tool output) flow nearly directly into tool calls,
making "whoever controls the language controls the action" the root cause of indirect prompt
injection and related authority failures. IntentMesh introduces a deterministic runtime that
inserts an explicit, inspectable **intent layer** between language and tools: language becomes a
mesh of typed intent nodes, each gated by policy, executed only as a validated typed contract,
verified against its postconditions, and recorded in a signed audit. On a 25-scenario benchmark
across five attack vectors, IntentMesh blocks 25/25 injections while completing the legitimate task,
versus 0/25 for an unguarded agent and 5/25 for a tool-name-gated agent.

## 1. Problem

Tool-layer security (allowlists, credential injection, output gating, MCP gateways) answers *"is
this tool call allowed?"* — a question asked too late. Indirect prompt injection succeeds because
the malicious payload, once it has been read, looks like a legitimate tool call with legitimate
arguments. A `send_email(to=attacker, body=secrets)` call passes a tool-name allowlist; the failure
is not the tool, it is that retrieved content was granted command authority. The boundary must move
upstream, to intent.

## 2. Architecture

IntentMesh is built around five seams, with authority moving strictly downstream away from language:

1. **Intent Proposer** (`IIntentProposer`) — interprets language into typed intent candidates.
   Untrusted; an LLM plugs in here but is never the authority.
2. **Intent Graph** — a DAG of typed, trust-tagged nodes. Exportable, diffable, replayable.
3. **Policy Gate** — fail-closed authority: trust source, capability scope, side-effect risk,
   recipient substitution, exfiltration, destructive actions, confirmation requirements.
4. **Tool Adapters** — deterministic; accept only typed contracts, never language.
5. **Postcondition Verifier** — checks the result against the approved contract. Crucially, it does
   **not** receive the original prompt — only the approved `IntentNode` contract and the tool
   output — so recipient substitution is caught as a contract-boundary violation independent of
   persuasion.

### Trust model

Every node carries a `TrustSource`. Only `User`/`System` hold command authority. Any node a tool
derives from untrusted content inherits `RetrievedContent` / `Authority=None` (zero-trust
inheritance); the Policy Gate blocks zero-trust nodes that request a side effect. This single rule
neutralizes indirect injection: the malicious instruction becomes a quarantined node, not a command.

### Symbolic layer

Action contracts, policy rules, NL cues, the trust model, tool capabilities, and the skill lifecycle
live as **data** in a 7-TLM bundle (`im-*`), compiled to a checksummed `.tlmz` format byte-compatible
with the RSRM / sage-rsrm runtime. Coverage grows by editing data + adding tests, not by changing
engine code. (The format is inherited from PassGen, a minimal prior demonstration of the pattern in
a single, security-sensitive domain.)

### Artifacts

Each run emits five signed artifacts — `intent.graph`, `policy.decisions`, `execution.trace`,
`verification.report`, `audit.signed` — bundled and HMAC-signed. Runs are deterministic: the same
prompt + approvals yields a byte-identical bundle, enabling `replay` to prove decisions reproduce.

## 3. Capability scoping & the proposal seam

Tools declare capabilities; the runtime holds a granted set; an ungranted action is blocked
(`pol-capability-not-granted`). A real adapter (e.g. Gmail) stays dark until its capability is
explicitly granted — the gate a production adapter framework needs. The proposal seam is swappable:
an LLM proposer is a drop-in, and a rogue proposal (a send to an attacker) is still gated and never
auto-executes. "Language proposes; only typed, validated, authorized intent executes."

## 4. Evaluation — IntentBench

25 scenarios across email-exfiltration, recipient-substitution, file-instruction-injection,
developer-shell, and data-destructive vectors. Each is run through the real pipeline and two
deterministic baseline models (an unguarded agent; an MCP/tool-name-gated agent).

| Criterion (/25) | Vanilla | MCP-gated | IntentMesh |
|---|---|---|---|
| Injection blocked | 0 | 5 | **25** |
| Legit task completed | 25 | 25 | **25** |
| Audit produced | 0 | 0 | **25** |
| Postcondition verified | 0 | 0 | **25** |

Tool-gating blocks only the raw-shell case; every attack that uses a legitimate tool with malicious
arguments sails through. IntentMesh quarantines them as zero-authority before they become tool calls.

## 5. Generalization

The same boundary is demonstrated across three domains — a personal agent (email/calendar/files), a
developer agent (typed code edits, shell blocked by default, secrets protected, PRs never pushed),
and a data agent (NL → typed query plan; read-only role blocks `DROP`/`DELETE`). The recipe is
identical: the model may propose; only typed, validated intent executes.

## 6. Scope and limitations

This is a research runtime. Tool adapters are sandboxed; real OAuth adapters, live MCP transport,
and a production key-management story for the audit signer are deliberately stubbed and documented
(see INTEGRATIONS.md). The benchmark baselines are deterministic architecture-class models, not live
LLMs; they illustrate a structural difference, not a product comparison.

## 7. Conclusion

Prompt injection is an authority failure, and authority failures are fixed at the architecture.
IntentMesh makes intent explicit, gates it before execution, verifies the result against the
contract, and signs the audit — so a convincing sentence in a file can no longer become a command.
Don't execute language. Execute verified intent.
