# Deploying the IntentMesh Control Room

> **Scope.** This covers running the Control Room web host (`IntentMesh.Web`). It enforces a real
> multi-tenant authz boundary (principal/tenant/role, tenant-isolated runs, server-issued approvals).
> What's still future: rate limiting/quotas and SSO/SCIM provisioning (see [MATURITY.md](MATURITY.md)).

## Run it

**Container (recommended):**
```bash
docker build -f src/IntentMesh.Web/Dockerfile -t intentmesh-controlroom .
docker run -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e INTENTMESH_AUDIT_KEY="$(openssl rand -base64 32)" \
  -e INTENTMESH_AUTH_KEY="$(openssl rand -base64 32)" \
  -e INTENTMESH_PRINCIPALS=/run/secrets/principals.json \
  -e "AllowedHosts=mesh.example.com" \
  -e INTENTMESH_RUNS_DIR=/data/runs \
  -v intentmesh-runs:/data/runs \
  intentmesh-controlroom
```

> Production uses **token mode** (above) or **trusted-proxy mode** (Mode B below). The legacy
> `INTENTMESH_WEB_TOKEN` is **not** accepted as a production auth boundary — the host refuses to start in
> Production unless token or proxy mode is configured.

**From source:** `dotnet run --project src/IntentMesh.Web` (Development; loopback, demo key).

## Required configuration (production)

| Variable | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT=Production` | Enables the production posture (the host **refuses to start with the demo audit key**). |
| `INTENTMESH_AUDIT_KEY` | Real ≥128-bit HMAC key (base64 or utf8) for signed audits. Source it from your secret store, never the image. |
| `INTENTMESH_AUDIT_KEY_ID` *(optional)* | Stable key id; `INTENTMESH_AUDIT_PRIOR_KEYS` (`id=base64;…`) for rotation — see [AUDIT-OPERATIONS.md](AUDIT-OPERATIONS.md). |
| `AllowedHosts` | Your public host(s), e.g. `mesh.example.com`. **Do not use `*`.** Default is loopback only. |
| `INTENTMESH_RUNS_DIR` | Where signed run bundles are persisted, partitioned per tenant under `t/{tenant}` (mount a durable volume). |

Plus one of the two authentication modes below. In Production the host **refuses to start without an
auth boundary** (set `INTENTMESH_ALLOW_INSECURE_AUTH=1` only for a deliberately local-only host).

## Authentication

### Mode A — built-in signed tokens (default)

| Variable | Purpose |
|---|---|
| `INTENTMESH_AUTH_KEY` | Dedicated ≥128-bit HMAC key (base64 or utf8) that signs session tokens and approval challenges. **Required for token mode in Production**, and kept separate from the audit key. |
| `INTENTMESH_PRINCIPALS` | Path to (or inline) a JSON principal directory. Each entry: `{ "id", "tenant", "roles": ["operator","approver","viewer"], "apiKeyHash" }` where `apiKeyHash` is the lowercase hex SHA-256 of the API key (the plaintext key is never stored). |

Flow: `POST /api/auth/token {"apiKey":"…"}` → `{ token }`. Present it on every `/api` call as
`X-Api-Token: <token>` (or `Authorization: Bearer <token>`). `GET /api/auth/whoami` echoes the
resolved principal/tenant/roles. Compute a hash with `PrincipalStore.HashApiKey` (e.g. via a small
console snippet) or `printf '%s' "$KEY" | sha256sum`.

### Mode B — trusted reverse-proxy / OIDC

| Variable | Purpose |
|---|---|
| `INTENTMESH_TRUSTED_PROXY=1` | Trust identity asserted by an upstream proxy/IdP instead of minting tokens. |
| `INTENTMESH_PROXY_SECRET` | Shared secret the proxy must present as `X-Proxy-Secret`. **Required in Production** (the host refuses to start in proxy mode without it) — without it, asserted headers would be honored from any loopback-presenting source. It also gates whether `X-Forwarded-For` is trusted for rate-limiting. |

The proxy authenticates the user (OIDC, etc.) and forwards `X-Auth-Principal`, `X-Auth-Tenant`, and a
comma-separated `X-Auth-Roles`. **The proxy MUST strip any client-supplied `X-Auth-*` / `X-Proxy-Secret`
/ `X-Forwarded-For` headers** so a caller can't spoof identity or its rate-limit bucket.

### Roles & isolation

`viewer` reads its tenant's runs; `operator` also runs the pipeline (`/api/run`, `/api/explain`,
`/api/export`); `approver` issues/consumes approval challenges; `admin` is a superset. A run id from
another tenant returns `404`. Caller-asserted approvals are ignored — approve a gated node by fetching
`POST /api/runs/{id}/challenges` and posting the returned challenge(s) to `POST /api/runs/{id}/approve`.

### Legacy single-token (dev/local only — NOT for production)

`INTENTMESH_WEB_TOKEN` is a single shared bearer mapped to a `default`-tenant principal with
**operator + viewer only** (deliberately *not* approver — a shared token must not be able to
self-approve gated nodes). It exists for local/dev convenience; **do not use it in production** — use
Mode A (token) or Mode B (proxy), which give per-principal identity and real approver separation.

## Hardening notes

- **Rate limiting** — a per-client fixed-window limiter (keyed on `X-Forwarded-For` from the proxy, else
  the socket IP) caps `/api` traffic; `POST /api/auth/token` has a stricter limit to blunt credential
  brute force. Behind a proxy, ensure it sets a truthful `X-Forwarded-For`.
- **Security headers** — every response carries a strict `Content-Security-Policy` (`script-src 'self'`),
  `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, and `Referrer-Policy: no-referrer`. The SPA
  keeps its bearer token in `sessionStorage` (tab-scoped), not `localStorage`.
- **Reads are role-gated** — every read endpoint requires at least the `viewer` role (operator/approver/
  admin also satisfy it); a principal with no recognized role cannot read run data.
- **Fail-closed persistence** — if a run (or an approved run) cannot be durably, verifiably stored, the
  API returns `503` rather than a `200` with an unsaved result.

## TLS / reverse-proxy contract

- Terminate **TLS at a reverse proxy** (nginx/Caddy/cloud LB) in front of the host; the app speaks
  plain HTTP on `:8080` inside the trust boundary.
- The proxy must set `Host` to your configured `AllowedHosts` value and forward to the container.
- Authenticate per the mode you configured above: forward the bearer **session token** (`X-Api-Token` /
  `Authorization: Bearer`) for token mode, or the verified `X-Auth-*` headers + `X-Proxy-Secret` for
  trusted-proxy mode. The proxy must **strip client-supplied `X-Auth-*` / `X-Proxy-Secret` / `X-Forwarded-For`**
  headers. (`INTENTMESH_WEB_TOKEN` is dev-only and is not a production boundary — see Authentication above.)

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
