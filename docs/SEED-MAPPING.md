# Seed Mapping — PassGen -> IntentMesh (T0.1)

A one-page record of how each PassGen component maps onto IntentMesh, produced from a close read of
the seed (`C:\Software\research\randomstringllm\PassGen`). This is the contract the build follows.

## The seed pipeline (verified by reading the code)

```
prompt --> TlmNlu.Resolve --> GenerateArgs/ConstraintSpec --> SpecValidator.Validate --> StringGenerator --> SpecValidator.CheckString + Entropy --> --trace
          (cues from TLM)    (typed intent)                  (fail-closed, throws)       (CSPRNG tool)       (verify)                              (audit)
```

Key facts that constrain our design:

- **The TLM library is self-contained and reusable.** `PassGen.Tlm` (TlmPackage, TlmManifest,
  TlmCompiler, TlmzEnvelope, TlmHasher, TlmValidator) is a faithful port of `Rsrm.Core.Models`.
  `System.Text.Json` serializes by **property name**, not namespace, so referencing this library (or
  vendoring it under a new namespace) keeps `.tlmz` **byte-identical** and checksum-compatible with
  live RSRM / sage-rsrm.
- **A TLM carries:** `Manifest{Metadata{TlmId,Role,Priority,Version,Checksum,HotSwapPolicy,
  StabilityScore},Imports,Derives,CreatedUtc,SchemaVersion}` + `Concepts[]` (Id,Label,Category,
  Description,Aliases,Properties) + `Relations[]` (SourceId,TargetId,Type,Description,Strength) +
  optional `Dimensions/Policies/Cues/FitSignals/Generators`.
- **Cues are the NL mechanism.** `SymbolicCue{Id,Trigger,Signal}`. `Trigger` is a `/`-separated
  synonym list; `Signal` is the action token (e.g. `q.min`, `target.length`, `only.<csv>`,
  `unsupported:<reason>`). `TlmNlu` loads every cue, groups phrases by signal, and builds regex
  matchers from them. **Coverage grows by editing TLM data, not code.**
- **`SymbolicPolicy{Id,Rule,Action}`** is the policy primitive (used by `rs-generation`). We reuse
  it directly for `im-policy-rules`.
- **Reproducible compiles:** source pins `CreatedUtc`; checksum is SHA-256 of compact JSON with the
  checksum field cleared. `tlm verify` proves byte-identical round-trip.
- **Fail-closed is a throw.** `SpecValidator.Validate` throws `SpecException` before the generator
  runs; the tool is never invoked on an infeasible spec.

## Component map

| PassGen | IntentMesh | Notes |
|---|---|---|
| `PassGen.Tlm` (library) | referenced as-is | the `.tlmz` format; do not reinvent |
| `PassGen.Tlm.Cli` / `DatasetAuthor` | `IntentMesh.Tlm.Cli` / `BundleAuthor` | author -> compile -> verify the `im-*` bundle |
| 7-TLM `rs-*` bundle | 7-TLM `im-*` bundle | trust-model, action-contracts, policy-rules, nl-vocabulary, tools, skills, bundle |
| `TlmNlu` | `IntentResolver` | loads cues from `im-nl-vocabulary`; emits typed intent nodes |
| `GenerateArgs` / `ConstraintSpec` | `TypedAction` records + `ContractRegistry` | strict, registry-bounded (Translation-Drift guard) |
| `SpecValidator.Validate` | `PolicyGate.Evaluate` | fail-closed authority; extended with trust + risk |
| `StringGenerator` (CSPRNG) | `IToolAdapter` set (sandboxed) | accept only typed contracts; operate on fake `Workspace` |
| `SpecValidator.CheckString` + `Entropy` | `PostconditionVerifier` | deterministic checks (Validation-Paradox guard) |
| `--trace` 5-panel | `IntentMesh.Cli --trace` + `IntentMesh.Web` Control Room | the mesh graph is the hero visual |

## Three edge-case guards (where they live)

1. **Translation Drift** -> `IntentResolver` may only emit `TypedAction` kinds present in
   `ContractRegistry` (loaded from `im-action-contracts`). No on-the-fly contracts. Mirrors
   PassGen: "numbers/classes are bound from context, never invented."
2. **State Poisoning** -> any node a tool adapter derives from untrusted content is stamped
   `TrustSource=RetrievedContent, Authority=None`. The `PolicyGate` blocks zero-trust nodes that
   request side effects. Data supplies content, never authority.
3. **Validation Paradox** -> `PostconditionVerifier` uses deterministic checks only (recipient
   equality, draft-not-sent, zero deletions, no attacker recipient, injected node never executed).

## Stack decision (T0.3 preview)

- `IntentMesh.Core` — the whole pipeline + fake workspace (class library).
- `IntentMesh.Tlm.Cli` — author/compile/verify the `im-*` bundle (references `PassGen.Tlm`).
- `IntentMesh.Cli` — `--trace` 5-panel + one-shot (PassGen-style console).
- `IntentMesh.Web` — ASP.NET minimal API serving a dependency-free SPA Control Room (vanilla JS +
  hand-rolled SVG mesh; no CDN, no npm build — robust offline).
- `IntentMesh.Tests` — xUnit, mirroring PassGen's targeted + matrix style.
