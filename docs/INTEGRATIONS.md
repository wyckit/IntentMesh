# IntentMesh — Integrations

This document describes the three integrations in `src/IntentMesh.Integrations`.

> **Status update — the prototype stubs have been converted to real implementations, and the
> documented "future" work is now shipped too.** The MCP transport (stdio **and** Streamable
> HTTP/SSE), OpenAPI parsing (JSON **and** YAML, with `$ref` resolution + semantic inference), and
> the email path (SMTP **and** the real OAuth device flow) are all genuinely functional and
> dependency-free. What remains "needs your setup" is only what is inherently yours to provide
> (credentials, a target server).

| Integration | What's now REAL | What still needs your setup |
|---|---|---|
| **McpProxy** | Real MCP **stdio** transport (`McpStdioClient`) **and Streamable HTTP/SSE** transport (`McpHttpClient`) behind one transport-agnostic `IMcpClient` seam — `GateAndForward` works over either. Wired in front of the **official `@modelcontextprotocol/server-filesystem`** — see [`MCP-FILESYSTEM.md`](MCP-FILESYSTEM.md) and `IntentMesh.McpDemo`: filesystem calls are gated by a **path-safety policy** + read/write rules before forwarding. **Per-server tool-mapping** via the `customMapper` constructor hook. | The URL/command of *your* target MCP server. |
| **OpenApiImporter** | Real OpenAPI 3.x parsing — **JSON or YAML** (dependency-free `MiniYaml`) — with **local `$ref` resolution** (`#/components/...`, incl. `allOf`) for parameters and request bodies, and **semantic inference** of side effect / risk / capability from operation keywords. Real registration (`RegisterToCompiledDir` compiles an `im-imported.tlmz` the runtime loads → the kind becomes enforced). | Nothing structural. Remote `$ref` (other files/URLs) and full YAML 1.2 (anchors/aliases/tags) are out of scope. |
| **Email adapter** | Real **SMTP** transport (`SmtpEmailTransport`, `System.Net.Mail`) — transmits when `SMTP_*` env is set (incl. Gmail SMTP + app password). Real **OAuth 2.0 device flow** (`GoogleDeviceCodeFlow`, RFC 8628, `HttpClient`-only) used by `AcquireTokenAsync` when `GOOGLE_OAUTH_CLIENT_ID` + `GOOGLE_OAUTH_CLIENT_SECRET` are set. Gated by the `email` capability + approval. | Your Google Cloud **OAuth client id + secret** and a human to approve consent — inherent to OAuth, not a stub. SMTP needs none. |

**Environment variables for the OAuth device flow**: `GOOGLE_OAUTH_CLIENT_ID`, `GOOGLE_OAUTH_CLIENT_SECRET`,
and optionally `GOOGLE_OAUTH_SCOPE` (defaults to `https://www.googleapis.com/auth/gmail.send`). With
them set, `AcquireTokenAsync` prints a verification URL + user code and polls Google until you
consent. Without them it falls back to `GMAIL_ACCESS_TOKEN`, then to a clear config error.

The sections below describe each integration's architecture and current behavior.

---

## Overview

The Integrations project (`IntentMesh.Integrations`) demonstrates how IntentMesh slots into
three real-world adoption paths:

| Integration | File | Adoption path |
|-----------|------|---------------|
| McpProxy | `McpProxy.cs` | MCP adapter / proxy mode |
| OpenApiImporter | `OpenApiImporter.cs` | Tool-schema import → typed contracts |
| OAuthAdapterExample | `OAuthAdapterExample.cs` | Real OAuth-backed `IToolAdapter` |

**Invariant across all three**: raw language never reaches a tool — only a typed, PolicyGate-approved
intent does. Real network I/O happens only when you configure a transport or credentials, and only
*after* the gate approves; with nothing configured the default transports record without transmitting
(safe for sandbox/demo runs).

---

## McpProxy

**File**: `src/IntentMesh.Integrations/McpProxy.cs`

**Architecture**: "MCP connects tools; IntentMesh verifies intent before tools."

The proxy sits *in front of* the MCP transport. When an MCP tool call arrives:

1. `MapToAction()` translates the MCP tool name + arguments to a typed IntentMesh action
   (`SendEmailAction`, `RunCommandAction`, etc.).
2. `McpOneNodeProposer` wraps the action as a single-node `ProposedPlan` and runs the full
   IntentMesh pipeline (propose → mesh → PolicyGate → typed adapter → postcondition verifier).
3. If the pipeline returns `Allowed`, the proxy calls `ForwardToRealMcpServer()` over the chosen transport.
4. If the pipeline returns `Block` or `NeedsConfirmation`, the forward is suppressed.

**Security invariant**: an MCP `send_email` to `attacker@evil.com` is gated by the PolicyGate
(external-comm confirmation rule) before any bytes could leave the process — the same gate
that governs the personal demo governs every MCP call.

### What is real

- All IntentMesh pipeline stages: proposal seam, PolicyGate evaluation, typed-adapter execution,
  postcondition verification.
- `MapToAction()` mapping logic for `send_email` → `SendEmailAction` and
  `run_command` → `RunCommandAction`.
- `McpOneNodeProposer` as a real `IIntentProposer` drop-in (v1.0 proposer seam).
- The fail-closed behaviour for unmapped tools.

### Transports (real)

`ForwardToRealMcpServer(call, IMcpClient)` forwards an approved call over either transport behind the
`IMcpClient` seam — `McpStdioClient` (newline-delimited JSON-RPC over a child process) or
`McpHttpClient` (Streamable HTTP/SSE, with an SSRF guard, a read deadline, and response size/event
caps). `GateAndForward` runs the gate and forwards **only** if IntentMesh approves — a blocked call
never reaches the server. The built-in map covers `send_email`, `run_command`, `read_calendar`, and
the `@modelcontextprotocol/server-filesystem` read/write tools (behind a pre-forward path-safety
policy); cover any other server's manifest with the `customMapper` constructor hook.

### Wiring it up

1. Connect a client: `McpStdioClient.Connect(...)` / `.ConnectNpx(...)`, or `McpHttpClient.Connect(url)`.
2. On each inbound tool call: `proxy.GateAndForward(call, client[, approvals])`.
3. The result carries the gate decision and — only when approved — the server's raw response.

---

## OpenApiImporter

**File**: `src/IntentMesh.Integrations/OpenApiImporter.cs`

**Architecture**: reads an external tool / OpenAPI-style operation schema and emits a typed
IntentMesh contract descriptor (`ImportedContract`) — the bridge between an existing tool
ecosystem and the IntentMesh typed-contract registry.

### Core types

- `ToolSchema` — an OpenAPI operation (name, method, summary, parameters, optional risk/side-effect
  hints). Parsed from a real spec by `ParseFromOpenApi`, or hand-authored.
- `ImportedContract` — typed contract descriptor from `ToContract()`: `Kind`, `Risk`, `SideEffect`,
  `Fields[]`, `RequiresConfirmation`, and `Capability` (the scoping capability inferred for the action).

### Parsing & mapping (real, deterministic, no LLM, no NuGet)

- `ParseFromOpenApi(spec)` accepts **JSON or YAML** (via the dependency-free `MiniYaml`), resolves
  local `$ref` pointers (`#/components/...`, incl. `allOf`) for parameters and request bodies, and
  folds `tags` into the summary. It is **fail-closed**: a remote/unresolvable `$ref`, over-deep
  nesting, tab-indented YAML, or oversized input throws rather than silently under-scoping a contract.
- `ToContract(schema)` maps deterministically: `Kind = act-{name}`; `SideEffect`/`Risk`/`Capability`
  are inferred from operation keywords (e.g. delete/refund → high; email/send → `email-send` + `email`;
  invoice/charge → `financial-write` + `billing`). A mutating operation with no recognized keyword
  rounds **up** to confirmation — never silently to `none`.
- `RegisterToCompiledDir(dir, contracts)` compiles a real `im-imported.tlmz` via `TlmCompiler`;
  `SymbolicBundle.Load(dir)` then recognizes the kind and the PolicyGate / Translation-Drift guard
  enforce it — import → usable typed contract, end-to-end.

### Sample schemas

- `OpenApiImporter.SampleInvoiceSchema` — `create_invoice` POST with `financial-write` →
  `RequiresConfirmation=true`.
- `OpenApiImporter.SampleGetCustomerSchema` — `get_customer` GET, no side effect →
  `RequiresConfirmation=false`.

### Limits

Remote `$ref` (other files/URLs) and full YAML 1.2 (anchors/aliases/tags) are out of scope — both are
rejected fail-closed rather than partially parsed.

---

## OAuthAdapterExample

**File**: `src/IntentMesh.Integrations/OAuthAdapterExample.cs`

**Architecture**: a clean example of how a real OAuth-backed adapter implements
`IToolAdapter` — the same interface as all built-in adapters (EmailAdapter, CalendarAdapter,
etc.).

### GmailSendAdapter

`GmailSendAdapter : IToolAdapter` handles `act-send-email`:

- **`Handles(kind)`**: returns `true` only for `Kinds.SendEmail`.
- **`RequiredCapability`**: `"email"` — the PolicyGate blocks this adapter's nodes when
  the `email` capability is not in the runtime's granted set (`pol-capability-not-granted`).
- **`Execute(node, decision, ws, approved)`**:
  1. If `approved == false` → halt, 0 messages transmitted.
  2. If `approved == true` → send via the injected `IEmailTransport`. With the default
     `NullEmailTransport` it records without transmitting; with `SmtpEmailTransport` (when `SMTP_*` is
     set) it sends for real.

The approval gate is *real* and must not be removed. It mirrors the built-in `EmailAdapter.Send()` pattern.

### Transports & token flow (real)

- `IEmailTransport` / `SmtpEmailTransport` (`System.Net.Mail`, no NuGet) transmits when `SMTP_*` is set
  — including Gmail SMTP with an app password (no OAuth needed). `NullEmailTransport` is the safe default.
- `GoogleDeviceCodeFlow` implements the real OAuth 2.0 Device Authorization Grant (RFC 8628) on
  `HttpClient`. `AcquireTokenAsync` uses it when `GOOGLE_OAUTH_CLIENT_ID` + `GOOGLE_OAUTH_CLIENT_SECRET`
  are set (optional `GOOGLE_OAUTH_SCOPE`): it prints a verification URL + user code and polls until you
  consent, otherwise it falls back to `GMAIL_ACCESS_TOKEN`, then a clear config error.

### Using it in production

1. **SMTP path (simplest)**: set `SMTP_HOST`/`SMTP_PORT`/`SMTP_USER`/`SMTP_PASS`/`SMTP_FROM` and pass a
   `SmtpEmailTransport` to `GmailSendAdapter`.
2. **OAuth device-flow path**: register an OAuth client in Google Cloud Console, set
   `GOOGLE_OAUTH_CLIENT_ID` + `GOOGLE_OAUTH_CLIENT_SECRET`, and complete the device-flow consent once.
3. **Register the adapter** behind `Kinds.SendEmail` and **grant** the `email` capability — the
   PolicyGate blocks the adapter until the capability is granted (`pol-capability-not-granted`).

---

## Test Coverage

Integration behavior is in `tests/IntentMesh.Tests/IntegrationTests.cs`; the kernel and transport
hardening is attacked adversarially in `tests/IntentMesh.Tests/IntentBenchRedTests.cs` (IntentBench-Red).
The full suite passes (111 tests at time of writing).

| Area | What the tests prove |
|------|----------------------|
| McpProxy gating | attacker email gated; benign email needs confirmation; unmapped tool blocked; command allow-list enforced; capability restrictions preserved through the proxy |
| MCP transports | stdio + Streamable HTTP/SSE forward an approved call to a real (in-process) server; a blocked call is never forwarded; SSRF + size/event caps; hostile error body truncated |
| OpenAPI import | JSON + YAML parse, `$ref` resolution, semantic side-effect/risk/capability inference; remote/unresolvable `$ref`, over-deep nesting, and tab indentation all fail closed |
| OAuth / email | capability scoping; no transmit without approval; the device-flow state machine (scripted handler — pending/slow_down/denied) |
| IntentBench-Red | audit tamper + demo-key forgery rejected; symlink/UNC/multi-path escapes blocked; risk-smuggling rejected; blanket approval refused; consent is provable from the signed audit |

Run with:

```
dotnet test tests/IntentMesh.Tests/IntentMesh.Tests.csproj
```

---

## Summary — every former stub is now real

For reference, here is the mapping from the original prototype stubs to the real, dependency-free
implementations now shipping.

| Former stub | Component | Real implementation now shipping |
|------|-----------|-------------------------------|
| `ForwardToRealMcpServer` | McpProxy | `IMcpClient` over stdio (`McpStdioClient`) **and** Streamable HTTP/SSE (`McpHttpClient`), `HttpClient`-only |
| `ParseFromOpenApi` | OpenApiImporter | System.Text.Json parser for JSON **and** YAML (`MiniYaml`), with local `$ref` resolution + semantic inference |
| `RegisterInBundle` | OpenApiImporter | `RegisterToCompiledDir` emits a real `im-imported.tlmz` via `TlmCompiler`; `SymbolicBundle.Load` enforces it |
| `AcquireTokenAsync` | OAuthAdapterExample | `GoogleDeviceCodeFlow` — real OAuth 2.0 Device Authorization Grant (RFC 8628), `HttpClient`-only |
| Gmail send | OAuthAdapterExample | Real `IEmailTransport` (`SmtpEmailTransport`, `System.Net.Mail`); transmits when `SMTP_*` is set |

Everything still flows through the same IntentMesh gate: language proposes, the PolicyGate decides,
and only typed, approved intent is forwarded over any transport.
