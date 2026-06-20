# IntentMesh.Tlm

The self-contained, dependency-free TLM bundle codec for
[IntentMesh](https://github.com/wyckit/IntentMesh) — the reader/writer for the symbolic `im-*.tlmz`
knowledge bundles (contracts, policy rules, cues) that `IntentMesh.Core` loads.

Vendored from `PassGen.Tlm` (a faithful port of `Rsrm.Core.Models`), so the `.tlmz` bytes and SHA-256
checksums are **byte-identical** to PassGen / live RSRM / sage-rsrm — no external runtime dependency.

> v1.7.0 — verified-intent platform **preview**. See
> [MATURITY.md](https://github.com/wyckit/IntentMesh/blob/master/docs/MATURITY.md).
