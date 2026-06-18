# IntentMesh Architecture

> **Don't execute language. Execute verified intent.**

IntentMesh implements **Symbolic Intent Architecture (SIA)** for agentic tool use. This document
describes the layers, the trust model, the data flow, and how the implementation maps back onto
its seed, PassGen.

## The boundary

The dominant agent pattern lets language — from the user, a retrieved document, a web page, or
another tool's output — flow more or less directly into a tool call. When language is the
authority, whoever controls the language controls the action. IntentMesh inserts an explicit,
inspectable boundary so that **authority moves downstream, away from language**:

```
  user request
    --> Language Resolver        interpret words -> propose typed intent candidates
    --> Intent Mesh              symbolic graph: typed nodes, trust edges, postconditions
    --> Typed Action Contracts   strict schemas selected from a bounded registry
    --> Policy / Risk Gate       allow / warn / confirm / review / block  (the authority)
    --> Tool Adapters            deterministic, sandboxed; accept only typed contracts
    --> Postcondition Verifier   prove the final state matches the approved intent
    --> Audit Trail              explain every decision
```

Each stage has exactly one job. The model is never the final authority.

## Layers

### 1. Language Resolver (`IntentResolver`)

Generalizes PassGen's `TlmNlu`. It loads **cues** (`Trigger` synonyms -> a `Signal`) from the
`im-nl-vocabulary` TLM and detects which signals fire in the request. The *words* live in the TLM
data; the *grammar* that composes signals into an action plan and binds entities (people, the gym,
the project folder) is generic code. Coverage grows by editing TLM data, not code.

Every emitted node carries `TrustSource = User`. The resolver may only emit action kinds present
in the `im-action-contracts` registry (**Translation-Drift guard**) — it never synthesizes a
contract on the fly, exactly as PassGen binds classes/numbers from context but never invents them.

### 2. Intent Mesh (`IntentGraph` / `IntentNode`)

The inspectable graph. Each node wraps a typed action and carries: id, type (action kind), label,
source phrase, **trust source + authority**, lifecycle status, the policy decision, the execution
result, audit references, and parent/child links. Nodes a tool proposes from untrusted content are
added here too — as zero-trust children.

### 3. Typed Action Contracts (`TypedAction` + `im-action-contracts`)

The agentic analog of PassGen's `ConstraintSpec`: strict, inspectable records — never prose. The
`im-action-contracts` TLM is the single source of truth for *what actions exist*, each declaring a
risk class, side-effect class, confirmation requirement, fields, and the postconditions it must
guarantee. `SymbolicBundle` loads this into a `ContractRegistry`.

### 4. Policy / Risk Gate (`PolicyGate` + `im-policy-rules`)

Generalizes `SpecValidator`. Fail-closed authority. For each node it emits a `PolicyDecision`
(decision, risk, reason, the rule ids that fired, confirmation requirement, trust source, and the
sensitive / external / destructive flags). The ordered rules live in `im-policy-rules` as
`SymbolicPolicy{Id, Rule, Action}` — the same primitive PassGen's `rs-generation` uses.

The decisive rule for injected content is `pol-zero-trust-side-effect`: **a node with
`Authority=None` that requests any side effect is blocked**, regardless of how convincing its text
is. We don't even have to reason about the recipient — but when present, `pol-recipient-
substitution` and `pol-private-exfiltration` are cited as additional rationale.

### 5. Tool Adapters (`IToolAdapter` + `im-tools`)

Deterministic, sandboxed, generalizing `StringGenerator`. Each adapter `Handles` a set of action
kinds and accepts **only a typed action** — never raw language. It honors the policy decision: a
`Confirm` decision performs only the safe, non-committing path (a calendar block is *staged*, a
send is *halted*, a delete *awaits approval*). Adapters mutate only the in-memory `Workspace`, so
every side effect is observable and reversible.

The `NotesAdapter` is where the injection surfaces: summarizing untrusted documents, it treats any
embedded imperative as **data** and quarantines it into a zero-trust *proposed* node, never an
instruction.

### 6. Postcondition Verifier (`PostconditionVerifier`)

Generalizes `SpecValidator.CheckString`. **Validation-Paradox guard: every check is
deterministic** — recipient equality, draft-not-sent, zero deletions, no attacker recipient,
injected-node-not-executed, block-proposed-not-committed. No semantic judgment by the planner. Only
the checks relevant to what actually happened are emitted.

### 7. Audit Trail (`AuditTrail`)

Generalizes `--trace`. Every resolve / policy / execute / verify decision, in order, human-readable
and expandable. The goal: anyone can ask "why was this blocked?" and get a clear answer.

## Trust model (`im-trust-model`)

| Trust source | Authority | Can command? |
|---|---|---|
| `User` | Full | yes (subject to policy + confirmation) |
| `System` | Full | yes |
| `RetrievedContent` | **None** | no — data only |
| `ToolOutput` | **None** | no — data only |

**Zero-Trust inheritance (the State-Poisoning guard):** any node created or modified by analyzing
untrusted data inputs inherits `TrustSource=RetrievedContent` and `Authority=None`. Data supplies
content; it can never spoof user intent or grant itself authority. The runtime re-runs every
proposed node through the *same* Policy Gate — which blocks it.

## The symbolic layer: the `im-*` TLM bundle

Authored in the `PassGen.Tlm` format and compiled to `.tlmz` (Brotli + SHA-256 envelope),
byte-compatible with live RSRM / sage-rsrm. Seven linked TLMs, mirroring PassGen's `rs-*` bundle:

| TLM | Role | Covers |
|-----|------|--------|
| `im-trust-model` | Foundation | trust sources, authority levels, the zero-trust rule |
| `im-action-contracts` | Logic | typed action schemas + postconditions |
| `im-policy-rules` | Policy | risk classes, decisions, the ordered policy rules |
| `im-nl-vocabulary` | Interface | English -> action signals (cues) |
| `im-tools` | Interface | adapter registry (contract -> tool) |
| `im-skills` | Overlay | emergent skill lifecycle scaffolding |
| `im-bundle` | Overlay | index + dependency DAG |

89 concepts, 100 relations, 204 parameters; 7/7 round-trip + checksum verify.

## The learning loop

New phrasings, contracts, and policies grow the symbolic layer as **data + tests**, never code:

```
  observed phrase --> proposed concept / cue / contract --> tests --> review --> versioned TLM update
```

The capacity to understand grows; the authority to act does not.

> **Emergence belongs in proposal and planning. Authority belongs in validation and execution.**

## How PassGen maps in, and how IntentMesh generalizes out

PassGen proved the pattern in one small, security-sensitive domain: language -> symbolic
constraints -> typed args -> validation -> deterministic execution -> verification. IntentMesh
keeps that skeleton **identical** and swaps one tool for many: the same resolve -> validate ->
execute -> verify -> audit flow, the same `.tlmz` symbolic layer, the same fail-closed discipline —
now governing calendars, email, files, and documents, with an explicit trust boundary that
neutralizes indirect prompt injection. Swap the rule-based resolver for an LLM proposer later and
nothing downstream changes: it still emits typed intent, still validated before anything runs.

See [SECURITY_MODEL.md](SECURITY_MODEL.md) for the threat model and [SEED-MAPPING.md](SEED-MAPPING.md)
for the component-by-component mapping.
