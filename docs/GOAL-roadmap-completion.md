# GOAL — Complete the IntentMesh roadmap (Phases 2–6)

> Slogan: **Don't execute language. Execute verified intent.**
> Build IntentMesh — the Verified Intent Runtime — to the end of the known roadmap. Phase 1 (public
> clarity) is done. Finish Phases 2–6, committing + pushing each phase to
> github.com/wyckit/IntentMesh, keeping `dotnet build` + `dotnet test` green and the TLM bundle 7/7
> throughout. Reuse the existing pipeline; do not fake real integrations — document what is stubbed.

## Done = all of these hold

**P2 — Runtime hardening**
- Versioned JSON for the five run artifacts (intent.graph / policy.decisions / execution.trace /
  verification.report / audit.signed) + JSON Schema files.
- One signed trace bundle (all five) exportable from the CLI and the Control Room.
- `intentmesh replay <bundle>`: reload, re-run, assert identical decisions (deterministic).
- Proposer/Verifier separation enforced in code: the Verifier cannot see the original prompt — only
  the approved IntentNode contract + raw ToolOutput; add a recipient-substitution contract-boundary test.
- Test suite ≥ 50.

**P3 — Control Room v1**
- Interactive mesh viewer: click a node for full detail; injected node turns red + quarantined live.
- Polished policy / verification / audit panels + audit timeline.
- One-click scenario selector (the 7 canonical attacks).
- Animated normal-vs-IntentMesh comparison; download-signed-bundle button.

**P4 — IntentBench v1**
- Benchmark schema + reproducible runner.
- 25 seed scenarios (5 each: email-exfil, recipient-substitution, file-injection, dev-shell, data-destructive).
- Baselines: a vanilla-agent and an MCP-gated stand-in to compare against.
- Scoreboard matrix (injection blocked / legit task done / audit produced / postcondition verified)
  + markdown report + a public benchmark page.

**P5 — Integration**
- Runtime SDK surface: propose → compileGraph → evaluatePolicy → executeTypedAction → verify →
  exportAudit, with a wrap-an-existing-agent example.
- MCP adapter/proxy prototype that sits in front of MCP tools.
- OpenAPI/MCP tool-schema import → typed intent contracts.
- A clean real-OAuth adapter example behind a capability grant (or a clearly documented stub).

**P6 — Launch**
- Manifesto "The Case for Verified Intent".
- 90-second launch video script (render if a recorder is available).
- Landing page, architecture whitepaper, "build your first adapter" guide.
- Tag releases per phase; final GitHub release.

## Invariants
- Every phase: build + tests green; TLM 7/7; commit + push; update ROADMAP/README status.
- Parallelize with the engram experts (runtime / control / bench / integration / launch) where work
  is independent; recall their seeded charters via cross_search.
- Never claim done if tests fail or a deliverable is stubbed — state what is real vs deferred.
- Keep the thesis intact: language proposes; only typed, validated, authorized intent executes;
  retrieved content is data, not authority; the action only counts if the result matches the
  approved intent.

## Success
The repo is a visual, benchmarked, reproducible proof of the Verified Intent Runtime: watch the
attack, see the intent graph, see the malicious node lose authority, see policy block it, see the
legit task succeed, see the verifier prove it, download the signed audit — and a public IntentBench
shows IntentMesh passing where vanilla and tool-gated agents fail.
