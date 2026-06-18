using IntentMesh.Core;
using IntentMesh.Integrations;

// mcp-fs-demo — wires the REAL @modelcontextprotocol/server-filesystem behind the IntentMesh proxy.
// Every filesystem call is gated by IntentMesh (path policy + read/write rules) BEFORE it is
// forwarded to the server. Usage: dotnet run --project src/IntentMesh.McpDemo [-- <sandbox-dir>]

string root = args.Length > 0 ? Path.GetFullPath(args[0])
    : Path.Combine(Path.GetTempPath(), "intentmesh-fs-sandbox");
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "note.txt"), "hello from the IntentMesh sandbox\n");

void Line() => Console.WriteLine(new string('-', 74));
Line();
Console.WriteLine("  IntentMesh × MCP filesystem — gate intent before the tool call");
Console.WriteLine($"  allowed root: {root}");
Line();

IntentMeshRuntime runtime;
try { runtime = IntentMeshRuntime.Load(); }
catch (Exception ex) { Console.Error.WriteLine($"Load failed: {ex.Message}"); return 1; }

McpStdioClient client;
try { client = McpStdioClient.ConnectNpx("@modelcontextprotocol/server-filesystem", root); }
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not start the filesystem MCP server (need node/npx + network): {ex.Message}");
    return 1;
}

using (client)
{
    Console.WriteLine($"server tools: {string.Join(", ", client.ListTools())}\n");
    var proxy = new McpProxy(runtime, Workspace.CreateDemo(), allowedRoot: root);

    void Show(string title, McpForwardResult r)
    {
        Console.WriteLine($"▸ {title}");
        Console.WriteLine($"    gate    : {(r.Gate.Allowed ? "ALLOWED → forwarded" : "BLOCKED → not forwarded")}");
        Console.WriteLine($"    reason  : {r.Gate.Reason}");
        if (r.ServerResponse is not null)
            Console.WriteLine($"    server  : {Clip(r.ServerResponse)}");
        Console.WriteLine();
    }

    // 1) A read inside the sandbox — allowed, forwarded, real content returned.
    Show("read_file note.txt (inside root)",
        proxy.GateAndForward(new McpToolCall("read_file", new Dictionary<string, string> { ["path"] = Path.Combine(root, "note.txt") }), client));

    // 2) A read OUTSIDE the sandbox — blocked by the path policy, never forwarded.
    var outside = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd";
    Show($"read_file {outside} (path traversal)",
        proxy.GateAndForward(new McpToolCall("read_file", new Dictionary<string, string> { ["path"] = outside }), client));

    // 3) A write WITHOUT approval — gated (NeedsConfirmation), not forwarded.
    var writeCall = new McpToolCall("write_file", new Dictionary<string, string> { ["path"] = Path.Combine(root, "out.txt"), ["content"] = "written through IntentMesh\n" });
    Show("write_file out.txt (no approval)", proxy.GateAndForward(writeCall, client));

    // 4) The same write WITH approval — forwarded, the real server writes the file.
    Show("write_file out.txt (approved)", proxy.GateAndForward(writeCall, client, new HashSet<string> { "n1" }));
    Console.WriteLine($"  out.txt exists on disk: {File.Exists(Path.Combine(root, "out.txt"))}");
}
return 0;

static string Clip(string s) { s = s.Replace("\n", " ").Trim(); return s.Length > 120 ? s[..119] + "…" : s; }
