# Authoring, Testing, Explaining, and Versioning a Policy Rule

The companion to [POLICY-AUTHORING.md](POLICY-AUTHORING.md) (which decides *the model*: fail-closed
C# `PolicyGate` predicates + generated symbolic citations, **no DSL**). This is the *workflow* for
working with rules in that model.

## The two halves of a rule

| Half | Where | Authoritative? |
|---|---|---|
| **Enforcement predicate** | `src/IntentMesh.Core/PolicyGate.cs` (C#) | **Yes** — this computes Allow / Confirm / Block, fail-closed. |
| **Citation metadata** | `im-policy-rules` (authored in `BundleAuthor.cs`, compiled to `.tlmz`) | No — a stable id + plain-English `Rule` (the condition) + `Action` (the decision + rationale), surfaced in every decision and in the Control Room's policy-evidence / why-blocked views. |

Each rule id the gate cites (`pol-zero-trust-side-effect`, `pol-command-not-allowlisted`, …) resolves
to a `RuleInfo(Id, Rule, Action)` via `PolicyCatalog.Find(bundle, ruleId)`. That's how a decision
becomes a human explanation without any prose hard-coded in the gate.

## Author a rule

1. **Predicate** — add the branch to `PolicyGate.Evaluate`. Default-deny on the risky path (a new
   side-effecting case should land on Block/Confirm, never silently Allow).
2. **Citation** — add a `SymbolicPolicy { Id, Rule, Action }` to `BundleAuthor.PolicyRules()`, then
   regenerate the bundle: `tlm author` → `tlm compile all` → `tlm verify`.
3. **Contract data, not parallel lists** — if the rule depends on a field, declare it in the
   `im-action-contracts` TLM and read it from `SymbolicBundle` (never a hand-coded list — see the
   lesson in this repo's history about required-field drift).

## Test a rule (fixtures)

`dataset/policy-fixtures.json` is the testable spec — each row asserts that a prompt produces a node
of a given kind with an expected decision citing an expected rule:

```json
{ "id": "deploy-not-allowlisted-blocked",
  "prompt": "Fix the failing test in the parser, run the tests, deploy to production, and open a pull request.",
  "expectKind": "act-run-command", "expectDecision": "Block", "expectRule": "pol-command-not-allowlisted" }
```

Run them:

```
intentmesh policy fixtures            # runs dataset/policy-fixtures.json, exits non-zero on any miss
```

`PolicyFixtureTests` runs the same set in CI and also asserts every cited rule exists in the catalog.
**A new rule should ship with a fixture row** that would fail if the rule were removed.

## Explain a rule

```
intentmesh policy list                # the full catalog: id, condition, decision + rationale
```

The same `RuleInfo` data backs the Control Room: a blocked/confirmed node's policy-evidence panel
renders the fired rule's `Rule` + `Action`, and the "why blocked" line is generated from it — no
separate explanation to maintain.

## Version / review a change

Policies are versioned with the compiled bundle. To review what a change did, diff the rule tables:

```
intentmesh policy diff dataset/compiled  /path/to/old/compiled
```

Output marks `+` added, `-` removed, `~` changed (with before/after). **A removed rule or a
Block→Allow / Block→Confirm loosening is a security-relevant change** — it should require explicit
sign-off, and the corresponding fixture row should change in the same commit. Re-running
`intentmesh policy fixtures` after the change shows which scenarios now decide differently.
