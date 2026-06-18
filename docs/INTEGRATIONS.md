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

The rest of this document is the original prototype write-up; the seam descriptions still hold, but
the "stubbed" notes are superseded by the table above.

---

## Overview

The Integrations project (`IntentMesh.Integrations`) demonstrates how IntentMesh slots into
three real-world adoption paths:

| Prototype | File | Adoption path |
|-----------|------|---------------|
| McpProxy | `McpProxy.cs` | MCP adapter / proxy mode |
| OpenApiImporter | `OpenApiImporter.cs` | Tool-schema import → typed contracts |
| OAuthAdapterExample | `OAuthAdapterExample.cs` | Real OAuth-backed `IToolAdapter` |

**Invariant across all prototypes**: no real network I/O occurs. Every stub is marked with a
`NotImplementedException` or a clearly-commented no-op. The architectural seams are production-
ready; only the external calls are deferred.

---

## McpProxy

**File**: `src/IntentMesh.Integrations/McpProxy.cs`

**Architecture**: "MCP connects tools; IntentMesh verifies intent before tools."

The proxy sits *in front of* the MCP transport. When an MCP tool call arrives:

1. `MapToAction()` translates the MCP tool name + arguments to a typed IntentMesh action
   (`SendEmailAction`, `RunCommandAction`, etc.).
2. `McpOneNodeProposer` wraps the action as a single-node `ProposedPlan` and runs the full
   IntentMesh pipeline (propose → mesh → PolicyGate → typed adapter → postcondition verifier).
3. If the pipeline returns `Allowed`, the proxy *would* call `ForwardToRealMcpServer()`.
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

### What is stubbed

| Stub | Method | Reason deferred |
|------|--------|-----------------|
| Real MCP stdio/SSE transport | `ForwardToRealMcpServer(McpToolCall)` | Requires the MCP .NET SDK, a live MCP server process, and network I/O — out of scope for in-process prototype |

`ForwardToRealMcpServer` always throws `NotImplementedException` with a message directing
implementors to the MCP .NET SDK.

### How it becomes production

1. Add the MCP .NET SDK package (or equivalent stdio/SSE library).
2. Establish a session with the target MCP server (stdio child process or SSE endpoint).
3. Implement `ForwardToRealMcpServer`: serialize the `McpToolCall` as a JSON-RPC
   `tools/call` message, await the response, return it to the original caller.
4. Extend `MapToAction()` to cover every tool in your MCP server's manifest.
5. Wire into the MCP server's request handler: `Gate(call)` → if `Allowed`, `ForwardToRealMcpServer(call)`.

---

## OpenApiImporter

**File**: `src/IntentMesh.Integrations/OpenApiImporter.cs`

**Architecture**: reads an external tool / OpenAPI-style operation schema and emits a typed
IntentMesh contract descriptor (`ImportedContract`) — the bridge between an existing tool
ecosystem and the IntentMesh typed-contract registry.

### Core types

- `ToolSchema` — minimal representation of an OpenAPI operation (name, method, summary,
  parameters, risk hint, side-effect hint). Hand-authored in this prototype; parsed from a
  real OpenAPI spec in production.
- `ImportedContract` — typed contract descriptor produced by `ToContract()`. Shape mirrors
  `ContractInfo` from the bundle: `Kind`, `Risk`, `SideEffect`, `Fields[]`,
  `RequiresConfirmation`.

### Mapping rules (deterministic, no LLM)

| Field | Rule |
|-------|------|
| `Kind` | `act-{name}` (hyphenated, lower-case) |
| `Risk` | Caller `RiskHint` if non-empty; else: DELETE→high, POST/PATCH→medium, GET→low |
| `SideEffect` | Caller `SideEffectHint` if non-empty; else "none" |
| `Fields` | Parameters list verbatim |
| `RequiresConfirmation` | true when method is POST/PUT/PATCH/DELETE AND SideEffect ≠ "none" |

### Sample schemas

- `OpenApiImporter.SampleInvoiceSchema` — `create_invoice` POST with `financial-write`
  side effect → `RequiresConfirmation=true`.
- `OpenApiImporter.SampleGetCustomerSchema` — `get_customer` GET, no side effect →
  `RequiresConfirmation=false`.

### What is real

- `ToContract(ToolSchema)` — full deterministic mapping, no external dependencies.
- The `ImportedContract` record as an inspectable contract descriptor.
- Both sample schemas and their expected contract outputs.

### What is stubbed

| Stub | Method | Reason deferred |
|------|--------|-----------------|
| Real OpenAPI YAML/JSON parsing | `ParseFromOpenApi(string openApiJson)` | Requires `Microsoft.OpenApi` NuGet package and a real spec file |
| Registration into live `SymbolicBundle` | `RegisterInBundle(SymbolicBundle, ImportedContract)` | `SymbolicBundle` is currently immutable; registration requires TLM source emission + recompile |

Both stubs throw `NotImplementedException`.

### How it becomes production

1. **Parse**: add `Microsoft.OpenApi`. Implement `ParseFromOpenApi` by reading
   `doc.Paths`, mapping each `OpenApiOperation` to a `ToolSchema`, and calling
   `ToContract()` on each.
2. **Register**: emit each `ImportedContract` as a TLM `ActionContract` concept into
   the appropriate `im-*.tlm` source file (or add a `SymbolicBundle.Register(ContractInfo)`
   hot-registration method).
3. **Compile**: run `tlm compile all` to produce an updated `.tlmz` bundle.
4. **Reload**: `SymbolicBundle.Load(compiledDir)` — the new kind is now recognized by the
   Translation-Drift guard, PolicyGate, and IntentResolver.

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
  2. If `approved == true` → run the stub (records in `ws.SentEmails`, no real I/O).

The approval gate is *real* and must not be removed. It mirrors the built-in
`EmailAdapter.Send()` pattern.

### What is real

- `Handles()` declaration and capability constant.
- The approval gate (`if (!approved) return Halt(...)`).
- The structural seam: OAuth token acquisition → Gmail API call → result.
- `OAuthAdapterWiringExample` showing the intended registration pattern.

### What is stubbed

| Stub | Method / location | Reason deferred |
|------|-------------------|-----------------|
| OAuth 2.0 token flow | `AcquireTokenAsync()` | Requires Google Cloud Console app registration, client secrets, browser redirect / device flow, secure token store |
| Real Gmail API call | Inside `Execute()`, clearly commented `// real OAuth-authenticated Gmail send goes here` | Requires `Google.Apis.Gmail.v1`, an access token, and a live Gmail account |

`AcquireTokenAsync()` throws `NotImplementedException`. The send path is a genuine no-op
(no bytes leave the process; only `ws.SentEmails` is updated for testability).

### How it becomes production

1. **Register the app**: Google Cloud Console → OAuth 2.0 credentials → download
   `client_secrets.json`.
2. **Implement `AcquireTokenAsync`**: use `Google.Apis.Auth` —
   `GoogleWebAuthorizationBroker.AuthorizeAsync(...)` for first login (stores refresh
   token in `FileDataStore`); subsequent calls silently refresh.
3. **Implement the send**: build a `GmailService` with the acquired credential; construct
   a MIME message; call `service.Users.Messages.Send(msg, "me").ExecuteAsync()`.
4. **Register the adapter**: extend `ToolHost` (or make it injectable) so
   `GmailSendAdapter` is returned by `ToolHost.For(Kinds.SendEmail)` instead of the
   built-in `EmailAdapter`.
5. **Grant the capability**: pass `grantedCapabilities` to `IntentMeshRuntime` that
   includes `"email"` — the PolicyGate enforces this gate before reaching the adapter.

---

## Test Coverage

Tests are in `tests/IntentMesh.Tests/IntegrationTests.cs` (17 tests, all passing).

| Group | Tests | What they prove |
|-------|-------|-----------------|
| McpProxy | 5 | attacker email is gated; benign email needs confirmation; unmapped tool blocked; allow-list blocks bad cmd; ForwardToRealMcpServer is stubbed |
| OpenApiImporter | 5 | invoice contract (medium/financial-write/RequiresConfirmation=true); GET (low/none/false); DELETE infers high; caller hint overrides; ParseFromOpenApi + RegisterInBundle stubbed |
| GmailSendAdapter | 7 | Handles only send-email; required capability matches bundle; no transmit without approval; stub no-op when approved; AcquireTokenAsync stubbed; capability scoping blocks when email not granted |

Run with:

```
dotnet test tests/IntentMesh.Tests/IntentMesh.Tests.csproj --filter "FullyQualifiedName~IntegrationTests"
```

---

## Summary — every former stub is now real

The prototype stubs below were all converted to working, dependency-free implementations. This table
supersedes the "What is stubbed" notes in the prototype write-up above.

| Former stub | Component | Real implementation now shipping |
|------|-----------|-------------------------------|
| `ForwardToRealMcpServer` | McpProxy | `IMcpClient` over stdio (`McpStdioClient`) **and** Streamable HTTP/SSE (`McpHttpClient`), `HttpClient`-only |
| `ParseFromOpenApi` | OpenApiImporter | System.Text.Json parser for JSON **and** YAML (`MiniYaml`), with local `$ref` resolution + semantic inference |
| `RegisterInBundle` | OpenApiImporter | `RegisterToCompiledDir` emits a real `im-imported.tlmz` via `TlmCompiler`; `SymbolicBundle.Load` enforces it |
| `AcquireTokenAsync` | OAuthAdapterExample | `GoogleDeviceCodeFlow` — real OAuth 2.0 Device Authorization Grant (RFC 8628), `HttpClient`-only |
| Gmail send | OAuthAdapterExample | Real `IEmailTransport` (`SmtpEmailTransport`, `System.Net.Mail`); transmits when `SMTP_*` is set |

Everything still flows through the same IntentMesh gate: language proposes, the PolicyGate decides,
and only typed, approved intent is forwarded over any transport.
