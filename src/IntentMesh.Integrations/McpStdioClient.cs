using System.Diagnostics;
using System.Text.Json;

namespace IntentMesh.Integrations;

/// <summary>
/// A real MCP client over the stdio transport: spawns an MCP server process and speaks
/// newline-delimited JSON-RPC 2.0, including the MCP handshake (initialize +
/// notifications/initialized), tools/list, and tools/call. This is the transport the McpProxy
/// forwards to once IntentMesh has approved the intent. No third-party SDK — just Process + stdio.
/// </summary>
public sealed class McpStdioClient : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private int _id;

    private McpStdioClient(Process proc)
    {
        _proc = proc;
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;
    }

    /// <summary>Spawn the server (<paramref name="command"/> + <paramref name="args"/>) and run the
    /// MCP initialize handshake. Throws if the server fails to respond to initialize.</summary>
    public static McpStdioClient Connect(string command, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start MCP server: {command}");
        var client = new McpStdioClient(proc);

        client.Request("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "intentmesh", version = "1.0.0" }
        });
        client.Notify("notifications/initialized");
        return client;
    }

    /// <summary>tools/list — the names of the tools the server exposes.</summary>
    public IReadOnlyList<string> ListTools()
    {
        var result = Request("tools/list", new { });
        var names = new List<string>();
        if (result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                if (t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    names.Add(n.GetString()!);
        return names;
    }

    /// <summary>tools/call — invoke a tool and return the raw JSON result. Only the McpProxy calls
    /// this, and only after IntentMesh has approved the intent.</summary>
    public string CallTool(string name, IReadOnlyDictionary<string, string> arguments)
        => Request("tools/call", new { name, arguments }).GetRawText();

    // ── JSON-RPC over stdio ───────────────────────────────────────────────────
    private JsonElement Request(string method, object @params)
    {
        int id = ++_id;
        _stdin.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }));
        _stdin.Flush();

        while (true)
        {
            var line = _stdout.ReadLine();
            if (line is null) throw new IOException("MCP server closed the connection.");
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || idEl.GetInt32() != id)
                continue; // a notification or unrelated message — skip
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"MCP error: {err.GetRawText()}");
            return root.GetProperty("result").Clone();   // Clone survives JsonDocument disposal
        }
    }

    private void Notify(string method)
    {
        _stdin.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method }));
        _stdin.Flush();
    }

    /// <summary>Locate the bundled mcp-echo-server.js by walking up to the repo root.</summary>
    public static string EchoServerScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "IntentMesh.Integrations", "mcp-echo-server.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("mcp-echo-server.js not found by walking up from the binary location.");
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        _proc.Dispose();
    }
}
