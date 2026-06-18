# Wiring a real MCP filesystem server behind IntentMesh

> MCP connects tools. IntentMesh verifies intent **before** the tool call.

`IntentMesh.McpDemo` wires the official **`@modelcontextprotocol/server-filesystem`** behind the
`McpProxy`. Every filesystem call is gated by IntentMesh — a **path-safety policy** plus the normal
read/write rules — *before* it is forwarded to the server over real stdio JSON-RPC.

## Run it

```bash
# needs node/npx + network (npx fetches the server)
dotnet run --project src/IntentMesh.McpDemo            # uses a temp sandbox dir
dotnet run --project src/IntentMesh.McpDemo -- C:\path\to\sandbox
```

Output (abridged):

```
server tools: read_file, write_file, list_directory, move_file, … (the real server's tools)

▸ read_file note.txt (inside root)        ALLOWED → forwarded   server: "hello from the IntentMesh sandbox"
▸ read_file C:\Windows\win.ini            BLOCKED → not forwarded   Blocked by path policy: escapes the allowed root
▸ write_file out.txt (no approval)        BLOCKED → not forwarded   Gated (NeedsConfirmation)
▸ write_file out.txt (approved)           ALLOWED → forwarded   server: "Successfully wrote to …out.txt"
  out.txt exists on disk: True
```

## How it works

1. An `McpToolCall` (`read_file` / `write_file` / `list_directory` / `move_file` / …) is **mapped**
   to a typed action: read-ish tools → `act-fs-read` (low, no side effect), write-ish tools →
   `act-fs-write` (medium, local-write, **confirmation required**).
2. **Path-safety policy:** if the call's `path` / `source` / `destination` resolves outside the
   proxy's `allowedRoot`, it is **blocked before the pipeline runs** — defense in depth over the
   server's own sandbox (fail-closed on unparseable paths).
3. The IntentMesh pipeline runs: `act-fs-read` → allow; `act-fs-write` → confirm (forwarded only
   with approval). Only if the node proceeds does `GateAndForward` call the real server via
   `McpStdioClient` (stdio JSON-RPC: initialize / tools/list / tools/call).
4. A blocked or unapproved call **never reaches the server** — `ServerResponse` is null.

## Wire your own MCP server

```csharp
using var client = McpStdioClient.ConnectNpx("@modelcontextprotocol/server-filesystem", sandboxRoot);
// ...or any server:  McpStdioClient.Connect("node", "my-server.js")  /  Connect("path/to/server", args)
var proxy = new McpProxy(runtime, Workspace.CreateDemo(), allowedRoot: sandboxRoot);
var result = proxy.GateAndForward(new McpToolCall("read_file", new() { ["path"] = file }), client);
if (result.Gate.Allowed) Console.WriteLine(result.ServerResponse);
```

Extend `McpProxy.MapToAction()` to map any tool in your server's `tools/list` manifest to a typed
contract; the gate governs it the moment it's mapped. Tests: `IntegrationTests` covers the path
policy + read/write gating (always), and `…wires_a_real_filesystem_mcp_server_end_to_end` runs the
real server when `INTENTMESH_FS_E2E=1` (kept off the default CI run to avoid the npm download).
