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
public sealed record McpGateResult(bool Allowed, string Reason, RunResult RunResult,
    // The approvals (verified challenge-attested node ids) actually applied to this run — recorded in the
    // signed audit so an approved forward's bundle carries its own approval header.
    IReadOnlyList<string>? AppliedApprovals = null);

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
    private readonly IRunArtifactStore? _auditStore;
    private readonly IAuditKeyProvider? _auditKeyProvider;
    private readonly ApprovalChallengeService? _approvalService;
    private readonly string _tenantId;

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
    /// <param name="auditStore">REQUIRED to forward: <see cref="GateAndForward"/> persists a signed
    /// <see cref="TraceBundle"/> here BEFORE the external call is made — a real MCP side effect can never
    /// occur without a durable, signed audit record. Fail-closed: if the audit can't be persisted, the call
    /// is NOT forwarded. A proxy constructed without it can still <see cref="Gate"/> (a pure decision) but
    /// <see cref="GateAndForward"/> throws.</param>
    /// <param name="auditKeyProvider">Signs the pre-forward audit bundle (required alongside
    /// <paramref name="auditStore"/>).</param>
    /// <param name="approvalService">REQUIRED to approve: MCP approvals are SERVER-ISSUED challenges bound
    /// to the exact call (tool + canonical args) + tenant + expiry — there is no raw-node-id path. Mint a
    /// token with <see cref="MintApprovalChallenge"/> and pass it as the approval. Supplying approvals
    /// without this service throws (a gated call simply stays blocked if nothing is approved).</param>
    /// <param name="tenantId">Tenant the proxy acts for (binds approval challenges + audit ownership).</param>
    public McpProxy(IntentMeshRuntime runtime, Workspace workspace, string? allowedRoot = null,
        Func<McpToolCall, (TypedAction? action, string label)?>? customMapper = null,
        IRunArtifactStore? auditStore = null, IAuditKeyProvider? auditKeyProvider = null,
        ApprovalChallengeService? approvalService = null, string? tenantId = null)
    {
        _runtime = runtime;
        _workspace = workspace;
        _allowedRoot = allowedRoot;
        _customMapper = customMapper;
        _auditStore = auditStore;
        _auditKeyProvider = auditKeyProvider;
        _approvalService = approvalService;
        _tenantId = tenantId ?? "default";
        if (_auditStore is not null && _auditKeyProvider is null)
            throw new ArgumentException("auditKeyProvider is required when auditStore is set (the pre-forward audit must be signed).", nameof(auditKeyProvider));
    }

    /// <summary>A deterministic fingerprint of a call (tool + canonical, key-sorted args) — the identity an
    /// approval challenge is bound to, so a challenge for one (tool, args) can't approve a different call.</summary>
    public static string CallFingerprint(McpToolCall call)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in call.Args) sorted[kv.Key] = kv.Value;
        var canonical = call.Tool + "\n" + JsonSerializer.Serialize(sorted);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Mint a server-issued approval challenge bound to this exact call + tenant + expiry. The
    /// returned token is the ONLY thing that approves the call's gated node — a raw node id cannot.
    /// Requires the proxy to be constructed with an <c>approvalService</c>.</summary>
    public string MintApprovalChallenge(McpToolCall call, long issuedAtUnix, long expiresAtUnix, string nonce)
    {
        if (_approvalService is null)
            throw new InvalidOperationException("This proxy was not constructed with an approvalService.");
        return _approvalService.Mint(CallFingerprint(call), "n1", _tenantId, issuedAtUnix, expiresAtUnix, nonce);
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

        // 2. Resolve approvals. An MCP approval is ALWAYS a SERVER-ISSUED challenge token bound to
        //    {this call's fingerprint, tenant, expiry} — there is NO raw-node-id path. Each valid token
        //    contributes the node id it attests; a "n1" string can never approve. Supplying approvals to a
        //    proxy with no approvalService is a configuration error (fail loud), not a silent raw-approve.
        var effectiveApprovals = approvals ?? (IReadOnlySet<string>)new HashSet<string>();
        if (effectiveApprovals.Count > 0)
        {
            if (_approvalService is null)
                throw new InvalidOperationException(
                    "MCP approvals must be server-issued challenges — construct McpProxy with an approvalService and pass " +
                    "tokens from MintApprovalChallenge. Raw node-id approvals are not accepted.");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fingerprint = CallFingerprint(call);
            var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in effectiveApprovals)
                if (_approvalService.TryVerify(token, fingerprint, _tenantId, now, out var approvedNode))
                    verified.Add(approvedNode);
            effectiveApprovals = verified;
        }

        // 3. Run through the full IntentMesh pipeline using a one-node proposer (with any approvals).
        //    RunWith preserves the caller's capability grants + approval cap — a proxy built on a
        //    capability-restricted runtime must gate under those same restrictions, not widen them.
        var proposer = new McpOneNodeProposer(node);
        var result = _runtime.RunWith(proposer, $"mcp:{call.Tool}", _workspace, effectiveApprovals);

        // 3. Decide from the node's final status. ALLOW-LIST, not deny-list: forward ONLY if the node
        //    actually proceeded (Allowed/Executed/Verified). Anything else — Blocked, NeedsConfirmation,
        //    and notably Halted (an approved action whose adapter failed) or any Pending/Resolved state —
        //    is NOT forwarded. A deny-list here would forward unexpected statuses (e.g. Halted).
        var policyView = result.Policy.FirstOrDefault(p => p.NodeId == "n1");
        var status = result.Nodes.FirstOrDefault(n => n.Id == "n1")?.Status ?? "Blocked";
        string reason = policyView is not null ? $"{policyView.Decision}: {policyView.Reason}" : "No policy decision recorded.";

        if (status is "Allowed" or "Executed" or "Verified")
            return new McpGateResult(Allowed: true, Reason: reason, RunResult: result, AppliedApprovals: effectiveApprovals.ToList());

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
        // Forwarding a real MCP call is a side effect — it REQUIRES a durable signed audit sink. There is
        // no audit-less forward path: a proxy not wired with one cannot forward.
        if (_auditStore is null || _auditKeyProvider is null)
            throw new InvalidOperationException(
                "GateAndForward requires a durable audit sink — construct McpProxy with an auditStore + auditKeyProvider " +
                "so every forwarded call is signed and persisted before it is made.");

        var gate = Gate(call, approvals);
        progress?.Report($"gate: {(gate.Allowed ? "allowed" : "blocked")} — {gate.Reason}");
        if (!gate.Allowed)
        {
            progress?.Report($"blocked {call.Tool} — not forwarded");
            return new McpForwardResult(gate, ServerResponse: null);
        }

        // Durable signed audit BEFORE the external side effect: persist a signed TraceBundle of the
        // approved gate decision first. FAIL-CLOSED — if the audit can't be written, the call is NOT
        // forwarded, so a real MCP side effect can never occur without a record.
        try
        {
            var bundle = TraceBundleBuilder.From(gate.RunResult, (gate.AppliedApprovals ?? Array.Empty<string>()).ToList(), _auditKeyProvider);
            var runId = _auditStore.Save(bundle);
            if (_auditStore is FileRunArtifactStore fs)
                fs.RecordOwner(runId, new RunOwner(_tenantId, _tenantId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            progress?.Report($"audited {call.Tool} (run {runId})");
        }
        catch (Exception ex)
        {
            progress?.Report($"blocked {call.Tool} — audit persistence failed, not forwarded");
            return new McpForwardResult(
                gate with { Allowed = false, Reason = $"Approved, but NOT forwarded: pre-forward audit could not be persisted ({ex.Message})." },
                ServerResponse: null);
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

    /// <summary>
    /// The EXACT argument keys forwarded to the server for each BUILT-IN tool — every other key is stripped
    /// before the call leaves the proxy, so an argument the typed mapping never represented (and the policy
    /// never checked) can't be honored unsigned/unchecked by the server. Each entry is the tool's FULL
    /// legitimate arg surface (so strictness doesn't break the tool). The filesystem entries track the
    /// pinned <c>@modelcontextprotocol/server-filesystem</c> version; bump them with the server. A
    /// custom-mapper tool that is NOT listed here is forwarded unchanged — the custom mapper owns its
    /// server's arg surface (and is responsible for its own allowlisting).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> ForwardArgAllowlist =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // Non-filesystem built-ins (small, fully-modeled surfaces).
            ["send_email"] = new[] { "to", "subject", "body" },
            ["run_command"] = new[] { "cmd" },
            ["read_calendar"] = new[] { "range" },
            // @modelcontextprotocol/server-filesystem tools.
            ["read_file"] = new[] { "path", "head", "tail" },
            ["read_text_file"] = new[] { "path", "head", "tail" },
            ["read_media_file"] = new[] { "path" },
            ["read_multiple_files"] = new[] { "paths" },
            ["get_file_info"] = new[] { "path" },
            ["list_directory"] = new[] { "path" },
            ["list_directory_with_sizes"] = new[] { "path", "sortBy" },
            ["directory_tree"] = new[] { "path", "excludePatterns" },
            ["search_files"] = new[] { "path", "pattern", "excludePatterns" },
            ["list_allowed_directories"] = Array.Empty<string>(),
            ["write_file"] = new[] { "path", "content" },
            ["edit_file"] = new[] { "path", "edits", "dryRun" },
            ["create_directory"] = new[] { "path" },
            ["move_file"] = new[] { "source", "destination" },
        };

    /// <summary>Normalize a call for forwarding: (1) for a filesystem call under a sandbox root, rewrite
    /// each path-bearing arg to the exact canonical in-root path the gate validated (so the server can't
    /// re-resolve a relative/aliased original to a different target than was checked); (2) for any built-in
    /// tool, strip every argument outside that tool's allowlist so an unrecognized/unchecked key can't be
    /// honored. A custom-mapper tool not in the allowlist is forwarded with its args intact.</summary>
    private McpToolCall NormalizeForForward(McpToolCall call)
    {
        var (action, _) = _customMapper?.Invoke(call) ?? MapToAction(call);
        var args = new Dictionary<string, string>(call.Args, StringComparer.Ordinal);

        if (_allowedRoot is not null && action is FsReadAction or FsWriteAction)
        {
            var root = Canonicalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_allowedRoot)));
            bool hadPath = false;
            foreach (var key in new[] { "path", "source", "destination" })
                if (args.TryGetValue(key, out var p) && !string.IsNullOrEmpty(p)) { args[key] = Resolve(p, root); hadPath = true; }

            // Custom-mapper paths: a per-server mapper may carry the path in a NON-standard arg (e.g.
            // "target"). The typed action exposes the validated path(s); rewrite whichever raw arg holds
            // that value to the canonical in-root path.
            foreach (var typed in TypedPaths(action))
            {
                var canonical = Resolve(typed, root);
                foreach (var key in args.Keys.ToList())
                    if (!string.IsNullOrEmpty(args[key]) && Resolve(args[key], root) == canonical) { args[key] = canonical; hadPath = true; }
            }
            // No-path filesystem tool under a sandbox: scope it explicitly to the root rather than letting
            // the server fall back to its own working directory (defense in depth over the server's sandbox).
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
        }

        // Forward-arg allowlist for every built-in tool — strip anything outside the tool's known surface.
        if (ForwardArgAllowlist.TryGetValue(call.Tool, out var allowed))
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var key in args.Keys.Where(k => !allowedSet.Contains(k)).ToList())
                args.Remove(key);
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
