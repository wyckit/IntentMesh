# Deploying the IntentMesh Control Room

> **Scope.** This covers running the Control Room web host (`IntentMesh.Web`) as a single-operator
> service behind your own boundary. It is **not** a multi-tenant SaaS deployment — there is no
> per-tenant authn/z, rate limiting, or managed key/secret service (see
> [MATURITY.md](MATURITY.md) for what's deliberately future).

## Run it

**Container (recommended):**
```bash
docker build -f src/IntentMesh.Web/Dockerfile -t intentmesh-controlroom .
docker run -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e INTENTMESH_AUDIT_KEY="$(openssl rand -base64 32)" \
  -e INTENTMESH_WEB_TOKEN="$(openssl rand -hex 24)" \
  -e "AllowedHosts=mesh.example.com" \
  -e INTENTMESH_RUNS_DIR=/data/runs \
  -v intentmesh-runs:/data/runs \
  intentmesh-controlroom
```

**From source:** `dotnet run --project src/IntentMesh.Web` (Development; loopback, demo key).

## Required configuration (production)

| Variable | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT=Production` | Enables the production posture (the host **refuses to start with the demo audit key**). |
| `INTENTMESH_AUDIT_KEY` | Real ≥128-bit HMAC key (base64 or utf8) for signed audits. Source it from your secret store, never the image. |
| `INTENTMESH_AUDIT_KEY_ID` *(optional)* | Stable key id; `INTENTMESH_AUDIT_PRIOR_KEYS` (`id=base64;…`) for rotation — see [AUDIT-OPERATIONS.md](AUDIT-OPERATIONS.md). |
| `INTENTMESH_WEB_TOKEN` | Bearer token required on every `/api` call from a **non-loopback** caller (a reverse proxy). Without it, `/api` is loopback-only. |
| `AllowedHosts` | Your public host(s), e.g. `mesh.example.com`. **Do not use `*`.** Default is loopback only. |
| `INTENTMESH_RUNS_DIR` | Where signed run bundles are persisted (mount a durable volume). |

The SPA sends the token from `localStorage['intentmesh_token']`; set it once in the browser console
for remote access.

## TLS / reverse-proxy contract

- Terminate **TLS at a reverse proxy** (nginx/Caddy/cloud LB) in front of the host; the app speaks
  plain HTTP on `:8080` inside the trust boundary.
- The proxy must set `Host` to your configured `AllowedHosts` value and forward to the container.
- Send the `INTENTMESH_WEB_TOKEN` as `X-Api-Token` (or `Authorization: Bearer`) — the app rejects
  non-loopback `/api` calls without it.

## Health checks

- **Liveness:** `GET /healthz` → `200 ok`.
- **Readiness:** `GET /readyz` → `200` when the bundle is loaded and the runs dir is writable, else `503`.

Both are unauthenticated (they carry no run data) and suitable for k8s `livenessProbe`/`readinessProbe`.

## Host hardening expectations

- Run the container as the non-root `app` user (the image does).
- Mount `INTENTMESH_RUNS_DIR` on durable storage; back it up. Artifacts are signed + tamper-evident
  but the file store is **cleartext and not encrypted-at-rest** — put it on an encrypted volume if the
  prompts/outputs are sensitive (a DB/blob/WORM backend is a future `IRunArtifactStore` implementation).
- Keep the audit key in a secret manager; rotate via `INTENTMESH_AUDIT_PRIOR_KEYS`.

## Reproducible builds

CI builds with the SDK pinned by [`global.json`](../global.json) (10.0.201) on a pinned runner, with
deterministic + SourceLink source-path normalization — two clean checkouts produce byte-identical
**assemblies**. (`.nupkg` containers carry archive timestamps; the DLL hashes are the reproducible
unit.) CI publishes SHA256SUMS and a build-provenance attestation for the packages.
