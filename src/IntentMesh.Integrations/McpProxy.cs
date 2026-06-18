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
//
// STILL FOR PRODUCTION (out of scope here):
//   • SSE/HTTP transport (a sibling client; the gate is transport-agnostic).
//   • MapToAction() coverage for every tool in a given server's manifest, and richer
//     argument coercion than the four mapped tools.
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

    /// <param name="runtime">
    /// A loaded IntentMeshRuntime. The caller controls which capabilities are
    /// granted — narrowing the grant is how you restrict which MCP tools may
    /// run.
    /// </param>
    /// <param name="workspace">
    /// The sandboxed workspace the adapter executes against. In a real
    /// integration this is replaced by a live adapter layer.
    /// </param>
    public McpProxy(IntentMeshRuntime runtime, Workspace workspace)
    {
        _runtime = runtime;
        _workspace = workspace;
    }

    /// <summary>
    /// Gates an inbound MCP tool call through the IntentMesh pipeline.
    /// Returns a <see cref="McpGateResult"/> describing whether the call is
    /// allowed or blocked. The caller MUST check <c>Allowed</c> before
    /// forwarding to a real MCP server.
    /// </summary>
    public McpGateResult Gate(McpToolCall call)
    {
        // 1. Map the MCP tool call to a typed IntentMesh action node.
        var (action, label) = MapToAction(call);
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

        // 2. Run through the full IntentMesh pipeline using a one-node proposer.
        var proposer = new McpOneNodeProposer(node);
        var scopedRuntime = new IntentMeshRuntime(_runtime.Bundle, proposer);
        var result = scopedRuntime.Run($"mcp:{call.Tool}", _workspace);

        // 3. Determine the gate outcome from the pipeline result.
        var policyView = result.Policy.FirstOrDefault(p => p.NodeId == "n1");
        bool blocked = policyView?.Decision == "Block"
                    || result.Nodes.FirstOrDefault(n => n.Id == "n1")?.Status == "Blocked";
        bool needsConfirmation = policyView?.Decision == "Confirm" && !blocked;

        string reason = policyView is not null
            ? $"{policyView.Decision}: {policyView.Reason}"
            : "No policy decision recorded.";

        if (blocked)
            return new McpGateResult(Allowed: false, Reason: reason, RunResult: result);

        if (needsConfirmation)
        {
            // In a production proxy, this would surface a confirmation request
            // to the MCP client or the human operator. The call is NOT forwarded
            // until explicit approval is received.
            return new McpGateResult(
                Allowed: false,
                Reason: $"Gated (NeedsConfirmation): {reason} — operator approval required before forwarding.",
                RunResult: result);
        }

        // 4. Allowed — the caller may now forward to the real MCP server.
        //    Use GateAndForward() to do both in one step.
        return new McpGateResult(Allowed: true, Reason: reason, RunResult: result);
    }

    /// <summary>
    /// Gate the call AND, only if IntentMesh approves it, forward it to a real MCP server over
    /// stdio. A blocked/gated call is never forwarded — no bytes reach the server. This is the
    /// production shape: the proxy verifies intent, then the transport runs.
    /// </summary>
    public McpForwardResult GateAndForward(McpToolCall call, McpStdioClient client)
    {
        var gate = Gate(call);
        if (!gate.Allowed) return new McpForwardResult(gate, ServerResponse: null);
        return new McpForwardResult(gate, ServerResponse: ForwardToRealMcpServer(call, client));
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
            _ => (null, string.Empty),
        };
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

    // ── REAL — MCP stdio transport ────────────────────────────────────────────
    /// <summary>
    /// Forwards an approved MCP tool call to a real MCP server over stdio (newline-delimited
    /// JSON-RPC 2.0 via <see cref="McpStdioClient"/>) and returns the server's raw JSON result.
    /// Called ONLY after <see cref="Gate"/> approves the action — the intent pipeline runs before
    /// any bytes leave the process. (SSE/HTTP transport would be a sibling client; the gate is
    /// transport-agnostic.)
    /// </summary>
    public static string ForwardToRealMcpServer(McpToolCall call, McpStdioClient client)
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
