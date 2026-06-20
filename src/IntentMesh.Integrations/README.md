# IntentMesh.Integrations

Real, dependency-free integrations for the [IntentMesh](https://github.com/wyckit/IntentMesh)
verified-intent runtime — everything here is gated by the kernel before any byte leaves the process.

- **MCP proxy** — `McpProxy` fronts a real MCP server over **stdio** or **Streamable HTTP/SSE**
  (`IMcpClient`); a blocked intent never reaches the server. `RetryingMcpClient` adds transient retry
  (read-only by default; tool-call retry is opt-in for idempotent tools). The stdio transport bounds
  read time and line size.
- **OpenAPI import** — map an OpenAPI 3.x spec (JSON or YAML, `$ref`-resolving) to typed contracts.
- **LLM proposer client** — `AnthropicLlmClient` behind the untrusted proposal seam.
- **Email/OAuth** — SMTP transport + OAuth 2.0 device flow (RFC 8628) with token-scope enforcement.

See the [integrations guide](https://github.com/wyckit/IntentMesh/blob/master/docs/INTEGRATIONS.md)
and [adapter guide](https://github.com/wyckit/IntentMesh/blob/master/docs/ADAPTER-GUIDE.md).

> v1.7.0 — verified-intent platform **preview**. See
> [MATURITY.md](https://github.com/wyckit/IntentMesh/blob/master/docs/MATURITY.md).
