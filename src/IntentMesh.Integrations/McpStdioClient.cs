using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IntentMesh.Integrations;

/// <summary>
/// A real MCP client over the stdio transport: spawns an MCP server process and speaks
/// newline-delimited JSON-RPC 2.0, including the MCP handshake (initialize +
/// notifications/initialized), tools/list, and tools/call. This is the transport the McpProxy
/// forwards to once IntentMesh has approved the intent. No third-party SDK — just Process + stdio.
/// </summary>
public sealed class McpStdioClient : IMcpClient
{
    /// <summary>How long to wait for a response line before giving up — bounds a hung/silent server
    /// so a forwarded tool call can't block the runtime forever (fail-closed against a dead transport).</summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Hard cap on a single JSON-RPC line, enforced WHILE reading (not after the whole line is
    /// buffered) so a server streaming an unbounded line can't exhaust memory first. Counted in chars
    /// (≈ bytes for the ASCII-dominant JSON-RPC framing); 8M is far above any legitimate response.</summary>
    public const int MaxLineChars = 8 * 1024 * 1024;

    private const int ReadChunkChars = 16 * 1024;

    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly TimeSpan _readTimeout;
    // Residual chars read past the last newline (a chunk read can overshoot into the next message);
    // carried to the next ReadLine so framing is preserved. Single-threaded request/response client.
    private readonly char[] _buf = new char[ReadChunkChars];
    private int _bufPos, _bufLen;
    private int _id;

    private McpStdioClient(Process proc, TimeSpan readTimeout)
    {
        _proc = proc;
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;
        _readTimeout = readTimeout;
    }

    /// <summary>Spawn the server (<paramref name="command"/> + <paramref name="args"/>) and run the
    /// MCP initialize handshake. Throws if the server fails to respond to initialize.</summary>
    public static McpStdioClient Connect(string command, params string[] args)
        => Connect(command, DefaultReadTimeout, args);

    /// <summary>Spawn the server with an explicit per-response read timeout (see DefaultReadTimeout).</summary>
    public static McpStdioClient Connect(string command, TimeSpan readTimeout, params string[] args)
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
        var client = new McpStdioClient(proc, readTimeout);
        client.StartDrainingStderr();   // a noisy server can't fill (and block on) the stderr pipe

        // If the handshake fails (server silent / crashes / times out), DISPOSE — which kills the child
        // process — instead of leaking it. Connect is the only place the process exists before the caller
        // gets a disposable handle, so it must clean up on its own failure.
        try
        {
            client.Request("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "intentmesh", version = "1.0.0" }
            });
            client.Notify("notifications/initialized");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    /// <summary>Drain the child's stderr to EOF on a background task and discard it — redirected stderr
    /// that is never read fills its pipe buffer and blocks a chatty server. Ends when the process exits
    /// (or is killed by <see cref="Dispose"/>).</summary>
    private void StartDrainingStderr()
        => _ = Task.Run(() => { try { while (_proc.StandardError.ReadLine() is not null) { } } catch { /* process gone */ } });

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

        using var deadline = new CancellationTokenSource(_readTimeout);
        while (true)
        {
            string? line;
            try { line = ReadLineBounded(method, deadline.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"MCP server did not answer '{method}' within {_readTimeout.TotalSeconds:0}s — abandoning the call (fail-closed).");
            }
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

    /// <summary>Read one newline-delimited line, enforcing <see cref="MaxLineChars"/> AS IT ACCUMULATES
    /// — chunks are read into a fixed buffer and the cap is checked before each refill, so a server
    /// streaming an unbounded line is cut off after ~cap+chunk chars instead of buffering it whole.
    /// Chars read past the newline are retained for the next call (framing-safe). Honors the read
    /// deadline via <paramref name="ct"/>; returns null at EOF.</summary>
    private string? ReadLineBounded(string method, CancellationToken ct)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_bufPos >= _bufLen)
            {
                _bufLen = _stdout.ReadAsync(_buf.AsMemory(), ct).AsTask().GetAwaiter().GetResult();
                _bufPos = 0;
                if (_bufLen == 0) return line.Length == 0 ? null : line.ToString();   // EOF
            }
            int nl = Array.IndexOf(_buf, '\n', _bufPos, _bufLen - _bufPos);
            if (nl >= 0)
            {
                line.Append(_buf, _bufPos, nl - _bufPos);
                _bufPos = nl + 1;
                if (line.Length > MaxLineChars) throw LineTooLong(method);
                return line.Length > 0 && line[^1] == '\r' ? line.ToString(0, line.Length - 1) : line.ToString();
            }
            // No newline in this chunk — take it all, enforce the cap, then refill.
            line.Append(_buf, _bufPos, _bufLen - _bufPos);
            _bufPos = _bufLen;
            if (line.Length > MaxLineChars) throw LineTooLong(method);
        }
    }

    private static IOException LineTooLong(string method)
        => new($"MCP server response line for '{method}' exceeded {MaxLineChars} chars — refusing to buffer it (fail-closed).");

    private void Notify(string method)
    {
        _stdin.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method }));
        _stdin.Flush();
    }

    /// <summary>Connect to an npm-published MCP server via npx (platform-aware: uses cmd.exe on
    /// Windows where <c>npx</c> is a .cmd). Example: ConnectNpx("@modelcontextprotocol/server-filesystem", root).</summary>
    public static McpStdioClient ConnectNpx(string package, params string[] serverArgs)
    {
        // On Windows the launch goes through cmd.exe /c, which RE-PARSES the command line — so any shell
        // metacharacter in the package name or args could run extra commands. Reject them up front (the
        // package name must look like an npm specifier; args must be metacharacter-free).
        if (!IsSafeNpmPackage(package))
            throw new ArgumentException($"Unsafe npm package specifier '{package}'.", nameof(package));
        foreach (var a in serverArgs)
            if (ContainsShellMeta(a))
                throw new ArgumentException($"Unsafe argument contains a shell metacharacter: '{a}'.", nameof(serverArgs));

        var args = new List<string> { "-y", package };
        args.AddRange(serverArgs);
        return OperatingSystem.IsWindows()
            ? Connect("cmd.exe", new[] { "/c", "npx" }.Concat(args).ToArray())
            : Connect("npx", args.ToArray());
    }

    private static readonly char[] ShellMeta = { ';', '|', '&', '$', '`', '\n', '\r', '>', '<', '(', ')', '{', '}', '"', '\'', '^', '%', '!', '*', '?' };
    private static bool ContainsShellMeta(string s) => s.IndexOfAny(ShellMeta) >= 0;

    /// <summary>A package specifier accepted for <c>npx -y</c> execution. Hardened beyond shell-safety:
    /// the name must START with an alphanumeric (so an option-shaped token like <c>-rf</c> or
    /// <c>--foo</c> can never be passed as the "package"), and it MUST carry a PINNED, digit-led version
    /// (<c>name@1.2.3</c>) — a floating spec (<c>name</c>, <c>name@latest</c>, <c>name@^1</c>) is rejected
    /// so npx can't resolve to an unexpected newly-published build at run time.</summary>
    private static bool IsSafeNpmPackage(string p)
        => !string.IsNullOrWhiteSpace(p) && !ContainsShellMeta(p) && !p.Contains(' ')
           && System.Text.RegularExpressions.Regex.IsMatch(
                p, @"^(@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*@\d+(\.\d+)*(-[a-z0-9.]+)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
