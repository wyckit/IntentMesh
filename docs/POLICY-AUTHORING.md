# Policy Authoring — Decision Doc

> How are IntentMesh policies authored, and should they become a declarative DSL?

This is a decision record. It states how policy works today, the options for evolving it, and the
recommendation for v1.6+. Status: **decided — keep the hybrid; defer a DSL.**

## How policy works today (be precise)

There are two layers, and it matters which is which:

1. **Enforcement logic — `src/IntentMesh.Core/PolicyGate.cs` (C#).** The actual decisions
   (`Allow` / `Confirm` / `Block`) are computed here, fail-closed, in audited, type-safe, unit-tested
   code: zero-trust + side-effect → Block; capability not granted → Block; shell not allow-listed →
   Block; destructive/external → Confirm; etc.
2. **Citable rule metadata — `im-policy-rules` TLM (data).** Each rule (`RuleInfo`: `id`, `rule`,
   `action`) gives the gate's decisions a stable, diff-able, human-readable *citation*
   (`pol-zero-trust-side-effect`, `pol-capability-not-granted`, …) that appears in the audit. The
   rule *text* is data; the rule *enforcement* is code.

So "policy as data" is **partial today**: the rule catalog and many parameters (capability grants,
the command allow-list, recipient/trust inputs) are data; the predicates that combine them are code.

## Options considered

### A — Status quo (C# gate + TLM citations)
- **Pros:** smallest trusted computing base; fail-closed by construction; fast; fully unit-testable;
  no interpreter to attack. Adding a rule is a reviewed code change with a test.
- **Cons:** a policy change requires a developer + recompile; non-engineers can't author; the
  "policy as data" story is only partially true.

### B — Declarative policy DSL compiled into the gate
- **Pros:** policies become first-class data — authored, versioned, diffed, hot-loaded; the gate
  becomes a thin interpreter; multi-tenant / per-deployment policy sets become possible.
- **Cons:** for a **security kernel**, a DSL is a new and dangerous surface. A bug in the evaluator
  or a gap in the language can **fail open** — the worst outcome for this product. It needs its own
  formal semantics, exhaustive tests, a safe-by-default evaluator, and an adversarial suite before it
  could be trusted. High effort; high risk; no current customer forcing it.

### C — Hybrid (formalize what we already have)
- Keep enforcement **predicates** in audited, fail-closed C#. Move policy **parameters** —
  allow-lists, capability grants, risk thresholds, recipient/trust rules — into TLM data where doing
  so cannot fail open. Each predicate stays small, named, and individually tested.
- **Pros:** improves the "policy as data" story incrementally without adding a fail-open surface;
  every step is testable; no big-bang rewrite.
- **Cons:** still not a free-form authoring experience; the predicate set is fixed in code.

## Decision

**Adopt C (hybrid); defer B (DSL).**

Rationale: for a fail-closed safety kernel, the cost of a DSL is not the implementation — it's the
new way to **fail open**. A declarative policy language only earns its place once there is a concrete
multi-policy / multi-tenant need that the hybrid genuinely cannot serve. Until then it is accidental
complexity and a fresh attack surface. The credible near-term work (v1.6) is the real end-to-end path
and benchmark, not a policy compiler.

## What this means concretely

- **v1.6:** no DSL. Continue to drive policy *parameters* from `im-policy-rules` / `im-tools` data;
  keep predicates in `PolicyGate`. Any new rule ships with (a) a TLM citation entry and (b) a test
  that fails if the rule is removed.
- **Guardrail for any future DSL (B):** it must be **fail-closed by default** (unknown construct →
  Block, never Allow), have a dedicated adversarial suite in the spirit of IntentBench-Red, and never
  widen authority that the C# predicates would deny. The DSL evaluates *within* the kernel's
  guarantees; it cannot relax them.

## Authoring a rule today (the supported path)

1. Add the predicate to `PolicyGate.Evaluate` (fail-closed; default deny on the risky branch).
2. Add the citation to `im-policy-rules` (id + rule text + action) and recompile the bundle.
3. Add a test asserting the decision (and that removing the predicate flips it) — see
   `ConfirmationTests`, `IntegrationTests`, `IntentBenchRedTests`.

See also: [ROADMAP.md](ROADMAP.md) (policy DSL listed as deliberately-future) and
[SECURITY_MODEL.md](SECURITY_MODEL.md) (the three guards the gate enforces).
