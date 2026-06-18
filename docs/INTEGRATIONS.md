# IntentMesh Phase 5 — Integration Prototypes

This document describes the three integration prototypes in `src/IntentMesh.Integrations`.
Each prototype defines a real architectural seam and explicitly lists what is stubbed and how
it becomes production.

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

## Summary of stubs

| Stub | Prototype | What replaces it in production |
|------|-----------|-------------------------------|
| `ForwardToRealMcpServer` | McpProxy | MCP .NET SDK stdio/SSE transport |
| `ParseFromOpenApi` | OpenApiImporter | `Microsoft.OpenApi` spec parser |
| `RegisterInBundle` | OpenApiImporter | TLM source emit + `tlm compile all` (or hot-registration API) |
| `AcquireTokenAsync` | OAuthAdapterExample | `Google.Apis.Auth` / MSAL token flow |
| Gmail API send (no-op) | OAuthAdapterExample | `Google.Apis.Gmail.v1` `Messages.Send` call |
