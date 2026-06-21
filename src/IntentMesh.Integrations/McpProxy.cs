using System.Text.Json;
using IntentMesh.Core;

namespace IntentMesh.Integrations;

// ──────────────────────────────────────────────────────────────────────────────
// McpProxy — "MCP connects tools; IntentMesh verifies intent before tools."
//
// WHAT IS REAL:
//   • All IntentMesh pipeline stages run for real: proposal → policy gate →
//     typed-adapter execution → postcondition verification.
//   • The mapping from MCP tool names to typed IntentMesh actions is real logic.
//   • The policy gate genuinely blocks/gates/allows based on the full bundle.
//   • The McpOneNodeProposer implements IIntentProposer — a drop-in proposer on
//     the real proposer seam (v1.0 framework hardening).
//
// NOW REAL (converted from the prototype stub):
//   • Real MCP stdio transport — ForwardToRealMcpServer(call, McpStdioClient) speaks
//     newline-delimited JSON-RPC 2.0 (initialize / tools/list / tools/call) to a real
//     server process. mcp-echo-server.js is a real minimal MCP server to gate against.
//   • GateAndForward() runs the gate and forwards ONLY if IntentMesh approves — a
//     blocked call never reaches the server.
//   • Transport-agnostic forwarding: GateAndForward/ForwardToRealMcpServer take an
//     IMcpClient, so the same gate fronts stdio (McpStdioClient) or Streamable
//     HTTP/SSE (McpHttpClient) without change.
//   • Per-server tool-mapping coverage: pass a customMapper to the constructor to map
//     any server's tool names to typed actions ahead of the built-in defaults.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// An inbound MCP tool call — the unit arriving from the MCP client before it
/// reaches any real MCP server transport. In a production MCP server this would
/// be deserialized from the JSON-RPC <c>tools/call</c> message.
/// </summary>
public sealed record McpToolCall(
    string Tool,
    IReadOnlyDictionary<string, string> Args);

/// <summary>
/// The result returned by <see cref="McpProxy.Gate"/>.
/// </summary>
/// <param name="Allowed">
/// <c>true</c> when IntentMesh approved the action and it executed (or was
/// staged). <c>false</c> when blocked — the MCP server MUST NOT forward the
/// call.
/// </param>
/// <param name="Reason">
/// Human-readable policy reason: why the call was allowed, gated, or blocked.
/// </param>
/// <param name="RunResult">
/// The full IntentMesh pipeline result (nodes, policy, audit) for inspection,
/// logging, or rendering in a control-room UI.
/// </param>
public sealed record McpGateResult(bool Allowed, string Reason, RunResult RunResult);

/// <summary>The outcome of <see cref="McpProxy.GateAndForward"/>: the gate decision, plus the real
/// MCP server's raw JSON response when (and only when) the call was approved and forwarded.</summary>
public sealed record McpForwardResult(McpGateResult Gate, string? ServerResponse);

/// <summary>
/// Internal one-node proposer: wraps a single pre-mapped typed action as the
/// IntentMesh proposer seam. This is the bridge between the McpProxy mapping
/// layer and the full IntentMeshRuntime pipeline.
/// </summary>
internal sealed class McpOneNodeProposer : IIntentProposer
{
    private readonly IntentNode _node;
    internal McpOneNodeProposer(IntentNode node) => _node = node;

    public ProposedPlan Propose(string prompt, Workspace ws) =>
        new(new[] { _node }, new[] { $"mcp-proxy:{_node.Type}" }, Array.Empty<string>());
}

/// <summary>
/// An in-process MCP proxy that gates every incoming MCP tool call through the
/// IntentMesh intent pipeline before the call would be forwarded to a real MCP
/// server. This is a <em>prototype</em> — the transport layer is explicitly
/// stubbed (see <see cref="ForwardToRealMcpServer"/>).
///
/// <para>
/// <strong>Architecture:</strong> the proxy sits in front of the MCP transport.
/// When a call arrives:
/// <list type="number">
///   <item>Map the MCP tool name + args → a typed IntentMesh action.</item>
///   <item>Run the IntentMesh pipeline (propose → mesh → policy → execute →
///         verify).</item>
///   <item>If and only if the pipeline returns <c>Allowed</c>, call
///         <see cref="ForwardToRealMcpServer"/> (which is stubbed here).</item>
///   <item>If blocked, return the block reason — the MCP server never sees the
///         call.</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>Security invariant:</strong> a <c>send_email</c> to
/// <c>attacker@evil.com</c> is blocked by the PolicyGate (pol-external-unknown
/// rule) before any forwarding occurs. The same gate that governs the personal
/// demo governs every MCP call.
/// </para>
/// </summary>
public sealed class McpProxy
{
    private readonly IntentMeshRuntime _runtime;
    private readonly Workspace _workspace;
    private readonly string? _allowedRoot;
    private readonly Func<McpToolCall, (TypedAction? action, string label)?>? _customMapper;

    /// <param name="runtime">
    /// A loaded IntentMeshRuntime. The caller controls which capabilities are
    /// granted — narrowing the grant is how you restrict which MCP tools may
    /// run.
    /// </param>
    /// <param name="workspace">
    /// The sandboxed workspace the adapter executes against. In a real
    /// integration this is replaced by a live adapter layer.
    /// </param>
    /// <param name="allowedRoot">When set, filesystem actions (act-fs-read/act-fs-write) whose path
    /// escapes this root are blocked by a path-safety policy BEFORE the pipeline runs and before any
    /// call is forwarded — defense in depth over the MCP filesystem server's own sandbox.</param>
    /// <param name="customMapper">Optional per-server mapping: given an inbound call, return the typed
    /// action + label to use, or <c>null</c> to fall through to the built-in mappings. This is how you
    /// cover every tool in a specific MCP server's manifest without editing the proxy.</param>
    public McpProxy(IntentMeshRuntime runtime, Workspace workspace, string? allowedRoot = null,
        Func<McpToolCall, (TypedAction? action, string label)?>? customMapper = null)
    {
        _runtime = runtime;
        _workspace = workspace;
        _allowedRoot = allowedRoot;
        _customMapper = customMapper;
    }

    /// <summary>
    /// Gates an inbound MCP tool call through the IntentMesh pipeline.
    /// Returns a <see cref="McpGateResult"/> describing whether the call is
    /// allowed or blocked. The caller MUST check <c>Allowed</c> before
    /// forwarding to a real MCP server.
    /// </summary>
    public McpGateResult Gate(McpToolCall call, IReadOnlySet<string>? approvals = null)
    {
        // 1. Map the MCP tool call to a typed IntentMesh action node (custom mapper first,
        //    then the built-in defaults). This is the per-server tool-mapping seam.
        var (action, label) = _customMapper?.Invoke(call) ?? MapToAction(call);
        if (action is null)
        {
            // Unmapped tools are blocked — unknown MCP tools must be
            // explicitly registered before they can be gated.
            return new McpGateResult(
                Allowed: false,
                Reason: $"MCP tool '{call.Tool}' is not mapped to any typed IntentMesh action. " +
                        "Register it in McpProxy.MapToAction() before use.",
                RunResult: EmptyResult(call.Tool));
        }

        // Path-safety policy: a filesystem call whose path escapes the allowed root is blocked here,
        // before the pipeline runs and before anything is forwarded to the MCP server. We check the
        // NORMALIZED typed action path(s) — FsReadAction/FsWriteAction.Path — AS WELL AS the raw
        // arguments. Checking the typed path is what closes the custom-mapper gap: a per-server mapper
        // that reads a non-standard argument name (e.g. "filepath") still produces a typed action whose
        // Path is enforced here, even though a raw-key scan over {path,source,destination,paths} would
        // miss it. The raw-arg scan remains as defense-in-depth for the multi-path `paths` array.
        if (_allowedRoot is not null && action is FsReadAction or FsWriteAction)
            foreach (var p in TypedPaths(action).Concat(CandidatePaths(call.Args)))
                if (PathEscapesRoot(p))
                    return new McpGateResult(
                        Allowed: false,
                        Reason: $"Blocked by path policy: '{p}' escapes the allowed root '{_allowedRoot}'.",
                        RunResult: EmptyResult(call.Tool));

        var node = new IntentNode
        {
            Id = "n1",
            Type = action.Kind,
            Label = label,
            Action = action,
            SourceText = $"mcp:{call.Tool}",
            TrustSource = TrustSource.User,
            Status = NodeStatus.Resolved,
        };

        // 2. Run through the full IntentMesh pipeline using a one-node proposer (with any approvals).
        //    RunWith preserves the caller's capability grants + approval cap — a proxy built on a
        //    capability-restricted runtime must gate under those same restrictions, not widen them.
        var proposer = new McpOneNodeProposer(node);
        var result = _runtime.RunWith(proposer, $"mcp:{call.Tool}", _workspace, approvals ?? new HashSet<string>());

        // 3. Decide from the node's final status. ALLOW-LIST, not deny-list: forward ONLY if the node
        //    actually proceeded (Allowed/Executed/Verified). Anything else — Blocked, NeedsConfirmation,
        //    and notably Halted (an approved action whose adapter failed) or any Pending/Resolved state —
        //    is NOT forwarded. A deny-list here would forward unexpected statuses (e.g. Halted).
        var policyView = result.Policy.FirstOrDefault(p => p.NodeId == "n1");
        var status = result.Nodes.FirstOrDefault(n => n.Id == "n1")?.Status ?? "Blocked";
        string reason = policyView is not null ? $"{policyView.Decision}: {policyView.Reason}" : "No policy decision recorded.";

        if (status is "Allowed" or "Executed" or "Verified")
            return new McpGateResult(Allowed: true, Reason: reason, RunResult: result);

        var detail = status == "NeedsConfirmation"
            ? $"Gated (NeedsConfirmation): {reason} — operator approval required before forwarding."
            : $"Not forwarded (status {status}): {reason}";
        return new McpGateResult(Allowed: false, Reason: detail, RunResult: result);
    }

    /// <summary>
    /// Gate the call AND, only if IntentMesh approves it, forward it to a real MCP server over
    /// stdio. A blocked/gated call is never forwarded — no bytes reach the server. This is the
    /// production shape: the proxy verifies intent, then the transport runs.
    /// </summary>
    /// <param name="progress">Optional progress sink for a control room / CLI: reports the gate
    /// decision and the forward lifecycle (<c>gate: …</c>, <c>forwarding …</c>, <c>forwarded …</c> or
    /// <c>blocked …</c>) as the call moves through the proxy. Verification still gates execution; this
    /// only narrates it.</param>
    public McpForwardResult GateAndForward(McpToolCall call, IMcpClient client,
        IReadOnlySet<string>? approvals = null, IProgress<string>? progress = null)
    {
        var gate = Gate(call, approvals);
        progress?.Report($"gate: {(gate.Allowed ? "allowed" : "blocked")} — {gate.Reason}");
        if (!gate.Allowed)
        {
            progress?.Report($"blocked {call.Tool} — not forwarded");
            return new McpForwardResult(gate, ServerResponse: null);
        }
        // Forward the NORMALIZED call: a filesystem path is rewritten to the exact canonical in-root
        // path the gate validated, so the server can't re-resolve a relative/aliased original arg to a
        // different (possibly escaping) target than what was checked (time-of-check/time-of-use).
        var forwardCall = NormalizeForForward(call);
        progress?.Report($"forwarding {call.Tool}");
        var response = ForwardToRealMcpServer(forwardCall, client);
        progress?.Report($"forwarded {call.Tool}");
        return new McpForwardResult(gate, ServerResponse: response);
    }

    /// <summary>For a filesystem call under an allowed root, rewrite each path-bearing arg to the exact
    /// canonical in-root path the gate validated — so the forwarded call can't re-resolve a relative or
    /// aliased original arg to a different target than was checked. Non-fs calls (or no allowed root)
    /// are forwarded unchanged.</summary>
    private McpToolCall NormalizeForForward(McpToolCall call)
    {
        if (_allowedRoot is null) return call;
        var (action, _) = _customMapper?.Invoke(call) ?? MapToAction(call);
        if (action is not (FsReadAction or FsWriteAction)) return call;

        var root = Canonicalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_allowedRoot)));
        var args = new Dictionary<string, string>(call.Args, StringComparer.Ordinal);
        bool hadPath = false;
        foreach (var key in new[] { "path", "source", "destination" })
            if (args.TryGetValue(key, out var p) && !string.IsNullOrEmpty(p)) { args[key] = Resolve(p, root); hadPath = true; }
        // No-path filesystem tool under a sandbox: scope it explicitly to the root rather than letting the
        // server fall back to its own working directory (defense in depth over the server's own sandbox).
        if (!hadPath && !args.ContainsKey("paths"))
            args["path"] = root;
        if (args.TryGetValue("paths", out var multi) && !string.IsNullOrWhiteSpace(multi))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(multi);
                if (parsed is not null)
                    args["paths"] = JsonSerializer.Serialize(parsed.Select(e => string.IsNullOrEmpty(e) ? e : Resolve(e, root)).ToList());
            }
            catch { /* not a JSON array — it was validated as a single path; leave as-is */ }
        }
        return call with { Args = args };

        static string Resolve(string p, string root)
            => Canonicalize(Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(root, p)));
    }

    /// <summary>
    /// Maps a raw MCP tool call to a typed IntentMesh action.
    /// Extend this method to cover every MCP tool in your server manifest.
    ///
    /// <para>
    /// Currently mapped:
    /// <list type="bullet">
    ///   <item><c>send_email</c> → <see cref="DraftEmailAction"/> +
    ///         <see cref="SendEmailAction"/> (gated by PolicyGate via the
    ///         <c>email</c> capability).</item>
    ///   <item><c>run_command</c> → <see cref="RunCommandAction"/> (blocked
    ///         unless the command is on the repo allow-list and approved).</item>
    /// </list>
    /// </para>
    /// </summary>
    private static (TypedAction? action, string label) MapToAction(McpToolCall call)
    {
        return call.Tool switch
        {
            "send_email" => MapSendEmail(call.Args),
            "run_command" => MapRunCommand(call.Args),
            "read_calendar" => (new ReadCalendarAction(call.Args.TryGetValue("range", out var r) ? r : "Friday"),
                                "MCP read_calendar"),
            // @modelcontextprotocol/server-filesystem tools (reads vs writes)
            "read_file" or "read_text_file" or "read_media_file" or "read_multiple_files"
                or "get_file_info" or "list_directory" or "list_directory_with_sizes"
                or "directory_tree" or "search_files" or "list_allowed_directories"
                => (new FsReadAction(call.Args.TryGetValue("path", out var rp) ? rp : "."), $"MCP {call.Tool} → {(call.Args.TryGetValue("path", out var rp2) ? rp2 : ".")}"),
            "write_file" or "edit_file" or "create_directory"
                => MapFsWrite(call),
            "move_file"
                => call.Args.TryGetValue("destination", out var dest)
                    ? (new FsWriteAction(dest, ""), $"MCP move_file → {dest}")
                    : ((TypedAction?)null, "move_file missing 'destination'"),
            _ => (null, string.Empty),
        };
    }

    private static (TypedAction?, string) MapFsWrite(McpToolCall call)
    {
        if (!call.Args.TryGetValue("path", out var path)) return (null, $"{call.Tool} missing 'path'");
        call.Args.TryGetValue("content", out var content);
        return (new FsWriteAction(path, content ?? ""), $"MCP {call.Tool} → {path}");
    }

    /// <summary>The path(s) carried by the NORMALIZED typed action — enforced regardless of which raw
    /// argument name a (possibly custom) mapper read them from.</summary>
    private static IEnumerable<string> TypedPaths(TypedAction action)
    {
        switch (action)
        {
            case FsReadAction r when !string.IsNullOrEmpty(r.Path): yield return r.Path; break;
            case FsWriteAction w when !string.IsNullOrEmpty(w.Path): yield return w.Path; break;
        }
    }

    /// <summary>Every path-bearing argument for a filesystem tool: the single-path keys plus the
    /// <c>paths</c> array (read_multiple_files). A <c>paths</c> value is parsed as a JSON array; if
    /// that fails it is treated as a single raw path (fail-closed — it still gets checked).</summary>
    private static IEnumerable<string> CandidatePaths(IReadOnlyDictionary<string, string> args)
    {
        foreach (var key in new[] { "path", "source", "destination" })
            if (args.TryGetValue(key, out var p) && !string.IsNullOrEmpty(p))
                yield return p;

        if (args.TryGetValue("paths", out var multi) && !string.IsNullOrWhiteSpace(multi))
        {
            List<string>? parsed = null;
            try { parsed = JsonSerializer.Deserialize<List<string>>(multi); } catch { /* not JSON */ }
            if (parsed is not null)
            {
                foreach (var e in parsed) if (!string.IsNullOrEmpty(e)) yield return e;
            }
            else yield return multi;
        }
    }

    /// <summary>True if <paramref name="path"/> resolves outside the configured allowed root.
    /// FAIL-CLOSED and link-aware: UNC and Win32 device-namespace prefixes are denied outright;
    /// symlinks/junctions are resolved to their final target (so a link inside the root that points
    /// outside is caught); the comparison uses the OS's path case sensitivity. An unparseable path
    /// counts as escaping.
    /// <para>KNOWN LIMITATIONS (defense in depth, not the sole control): this is a time-of-check
    /// gate — the path could in principle be re-pointed between this check and the server's later
    /// open() (TOCTOU), and Windows 8.3 short-name aliases are not long-name expanded here. The real
    /// MCP filesystem server's own sandbox remains the authoritative enforcement; this check is an
    /// independent pre-forward layer over it.</para></summary>
    private bool PathEscapesRoot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            // UNC (\\server\share, //server) and device (\\?\, \\.\) prefixes bypass normal root logic.
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
                || path.Contains(@"\\?\", StringComparison.Ordinal) || path.Contains(@"\\.\", StringComparison.Ordinal))
                return true;

            var root = Canonicalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_allowedRoot!)));
            var full = Canonicalize(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path)));
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return !(full.Equals(root, cmp) || full.StartsWith(root + Path.DirectorySeparatorChar, cmp));
        }
        catch { return true; }
    }

    /// <summary>Resolve every symlink/junction on the path to its final on-disk target — including a
    /// symlinked *directory* component, not just the leaf — so a link anywhere in the chain that
    /// points outside the root is caught. The parent chain is canonicalized first, then the leaf;
    /// a not-yet-existing leaf (write target) re-attaches to the canonicalized parent. Depth-guarded
    /// against symlink cycles.</summary>
    private static string Canonicalize(string fullPath, int depth = 0)
    {
        if (depth > 64) return Path.TrimEndingDirectorySeparator(fullPath);
        try
        {
            var parent = Path.GetDirectoryName(fullPath);
            var rebuilt = fullPath;
            if (parent is not null && parent.Length > 0 && parent != fullPath)
            {
                var canonicalParent = (Directory.Exists(parent) || File.Exists(parent)) ? Canonicalize(parent, depth + 1) : parent;
                rebuilt = Path.Combine(canonicalParent, Path.GetFileName(fullPath));
            }

            FileSystemInfo? info = Directory.Exists(rebuilt) ? new DirectoryInfo(rebuilt)
                                 : File.Exists(rebuilt) ? new FileInfo(rebuilt) : null;
            if (info is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
            }
            return Path.TrimEndingDirectorySeparator(rebuilt);
        }
        catch { /* fall through to lexical form */ }
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static (TypedAction?, string) MapSendEmail(IReadOnlyDictionary<string, string> args)
    {
        // Map "to" arg to a typed SendEmailAction. We use DraftRef="mcp-draft"
        // as a sentinel; in production this would reference an actual draft id.
        if (!args.TryGetValue("to", out var recipient))
            return (null, "send_email missing 'to' argument");
        args.TryGetValue("subject", out var subject);
        args.TryGetValue("body", out var _);

        // We use SendEmailAction directly so the PolicyGate applies the full
        // external-send rules (pol-external-unknown, pol-send-to-unknown-contact,
        // pol-send-requires-confirmation, etc.).
        return (
            new SendEmailAction(
                DraftRef: "mcp-draft",
                Recipient: recipient,
                BodySourceRefs: Array.Empty<string>()),
            $"MCP send_email → {recipient} ({subject ?? "no subject"})");
    }

    private static (TypedAction?, string) MapRunCommand(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("cmd", out var cmd))
            return (null, "run_command missing 'cmd' argument");
        return (new RunCommandAction(cmd), $"MCP run_command → {cmd}");
    }

    // ── REAL — transport-agnostic forwarding (stdio or Streamable HTTP/SSE) ───
    /// <summary>
    /// Forwards an approved MCP tool call to a real MCP server through any <see cref="IMcpClient"/>
    /// transport — stdio (<see cref="McpStdioClient"/>) or Streamable HTTP/SSE
    /// (<see cref="McpHttpClient"/>) — and returns the server's raw JSON result. Called ONLY after
    /// <see cref="Gate"/> approves the action: the intent pipeline runs before any bytes leave the
    /// process, regardless of transport.
    /// </summary>
    public static string ForwardToRealMcpServer(McpToolCall call, IMcpClient client)
        => client.CallTool(call.Tool, call.Args);

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static RunResult EmptyResult(string toolName) => new(
        Prompt: $"mcp:{toolName}",
        ResolverFired: Array.Empty<string>(),
        Unsupported: new[] { $"unmapped tool: {toolName}" },
        Nodes: Array.Empty<NodeView>(),
        Policy: Array.Empty<PolicyView>(),
        Execution: Array.Empty<ExecView>(),
        Verification: Array.Empty<VerifyView>(),
        Audit: Array.Empty<AuditView>(),
        Summary: new SummaryView(0, 0, 0, 0, 0, 0),
        Skills: new SkillsView(Array.Empty<string>(), Array.Empty<SkillView>()));
}
