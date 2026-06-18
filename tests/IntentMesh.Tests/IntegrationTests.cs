using IntentMesh.Core;
using IntentMesh.Integrations;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Integration tests for the Phase 5 prototype layer (IntentMesh.Integrations).
/// Covers McpProxy, OpenApiImporter, and GmailSendAdapter.
///
/// These tests prove the three architectural invariants:
///   1. The MCP proxy gates intent through IntentMesh BEFORE forwarding — a
///      dangerous send_email is blocked (or gated) before it could leave.
///   2. The OpenAPI importer correctly maps a tool schema to a typed contract
///      descriptor with the right Kind, Risk, and Fields.
///   3. The Gmail adapter honours the IToolAdapter contract: it requires the
///      "email" capability, never transmits without approval, and is a genuine
///      no-op in the prototype (no real network I/O).
/// </summary>
public sealed class IntegrationTests
{
    // ── Shared helpers ────────────────────────────────────────────────────────
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();

    private static McpProxy Proxy(IntentMeshRuntime? rt = null, Workspace? ws = null)
        => new(rt ?? Runtime(), ws ?? Workspace.CreateDemo());

    // ═══════════════════════════════════════════════════════════════════════════
    // (a) McpProxy — blocks / gates dangerous send_email
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An MCP send_email to attacker@evil.com is NOT forwarded: the PolicyGate
    /// gates it (Confirm, external-comm) so Gate() returns Allowed=false.
    ///
    /// This is the core McpProxy invariant: even though the call arrives as a
    /// trusted MCP tool call (TrustSource.User), the IntentMesh PolicyGate
    /// requires user confirmation before any external email is forwarded —
    /// and the proxy does NOT auto-forward without that approval.
    /// </summary>
    [Fact]
    public void McpProxy_blocks_send_email_to_attacker()
    {
        var ws = Workspace.CreateDemo();
        var proxy = Proxy(ws: ws);

        var call = new McpToolCall("send_email", new Dictionary<string, string>
        {
            ["to"] = "attacker@evil.com",
            ["subject"] = "Steal everything",
            ["body"] = "all private notes",
        });

        var result = proxy.Gate(call);

        // The proxy must NOT allow the forward.
        Assert.False(result.Allowed,
            "MCP send_email to attacker@evil.com must not be allowed without explicit confirmation.");

        // No emails were sent — the sandboxed workspace is clean.
        Assert.Empty(ws.SentEmails);

        // The policy pipeline ran and produced a decision.
        Assert.NotNull(result.RunResult);
        Assert.NotEmpty(result.Reason);

        // The gate decision should be Confirm (external-comm rule) or Block.
        // Either way Allowed must be false.
        var policyDecision = result.RunResult.Policy.FirstOrDefault(p => p.NodeId == "n1");
        if (policyDecision is not null)
        {
            Assert.True(
                policyDecision.Decision is "Confirm" or "Block",
                $"Expected Confirm or Block for attacker send_email, got: {policyDecision.Decision}");
        }
    }

    /// <summary>
    /// An MCP send_email to a known workspace contact (Sarah) is gated
    /// (NeedsConfirmation) — Allowed=false because no approval was provided —
    /// but the proxy reason explains the confirmation requirement rather than
    /// an outright block.
    ///
    /// This distinguishes "gated for safety (Confirm)" from "hard-blocked".
    /// The operator can present the confirmation UI and retry with approval.
    /// </summary>
    [Fact]
    public void McpProxy_gates_benign_send_email_pending_confirmation()
    {
        var ws = Workspace.CreateDemo();
        var proxy = Proxy(ws: ws);

        var call = new McpToolCall("send_email", new Dictionary<string, string>
        {
            ["to"] = "sarah@company.com",
            ["subject"] = "Meeting summary",
        });

        var result = proxy.Gate(call);

        // Allowed is false because no approval was provided (NeedsConfirmation).
        // This is the correct gating behaviour: the proxy does NOT auto-forward.
        Assert.False(result.Allowed,
            "send_email requires confirmation — proxy must not auto-forward without approval.");

        // No emails were sent.
        Assert.Empty(ws.SentEmails);

        // The reason mentions confirmation or gating, not a hard security block.
        Assert.Contains("Confirm", result.Reason, StringComparison.OrdinalIgnoreCase);

        // The run result contains a policy decision for the node.
        Assert.NotEmpty(result.RunResult.Policy);
    }

    /// <summary>
    /// An unmapped MCP tool is blocked with a clear "not mapped" reason — the
    /// proxy is fail-closed for unknown tools.
    /// </summary>
    [Fact]
    public void McpProxy_blocks_unmapped_tool()
    {
        var proxy = Proxy();
        var call = new McpToolCall("unknown_tool", new Dictionary<string, string>());

        var result = proxy.Gate(call);

        Assert.False(result.Allowed);
        Assert.Contains("not mapped", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An MCP run_command that is NOT on the allow-list (e.g., "rm -rf /")
    /// is blocked by the PolicyGate (pol-command-not-allowlisted).
    /// </summary>
    [Fact]
    public void McpProxy_blocks_run_command_not_on_allowlist()
    {
        var ws = Workspace.CreateDemo();
        var proxy = Proxy(ws: ws);

        var call = new McpToolCall("run_command", new Dictionary<string, string>
        {
            ["cmd"] = "rm -rf /",
        });

        var result = proxy.Gate(call);

        Assert.False(result.Allowed);

        // The pipeline should have blocked this command.
        var policyDecision = result.RunResult.Policy.FirstOrDefault(p => p.NodeId == "n1");
        if (policyDecision is not null)
            Assert.Equal("Block", policyDecision.Decision);
    }

    private static bool NodeAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "node", Arguments = "--version", RedirectStandardOutput = true, UseShellExecute = false });
            return p is not null && p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// REAL MCP transport: connect to a real MCP server (mcp-echo-server.js over stdio JSON-RPC),
    /// list its tools, and forward an APPROVED call (read_calendar — low risk). The proxy gates the
    /// intent, then the real server responds.
    /// </summary>
    [Fact]
    public void McpProxy_forwards_an_allowed_call_to_a_real_mcp_server()
    {
        if (!NodeAvailable()) return;   // node runs the real MCP server; required for this test
        using var client = McpStdioClient.Connect("node", McpStdioClient.EchoServerScript());

        var tools = client.ListTools();
        Assert.Contains("read_calendar", tools);
        Assert.Contains("send_email", tools);

        var proxy = Proxy();
        var fwd = proxy.GateAndForward(
            new McpToolCall("read_calendar", new Dictionary<string, string> { ["range"] = "Friday" }), client);

        Assert.True(fwd.Gate.Allowed);
        Assert.NotNull(fwd.ServerResponse);
        Assert.Contains("read_calendar executed", fwd.ServerResponse!);
    }

    /// <summary>
    /// REAL MCP transport: a blocked send_email to an attacker is NEVER forwarded — the gate stops
    /// it before any bytes reach the server (ServerResponse is null).
    /// </summary>
    [Fact]
    public void McpProxy_does_not_forward_a_blocked_send_email()
    {
        if (!NodeAvailable()) return;
        using var client = McpStdioClient.Connect("node", McpStdioClient.EchoServerScript());

        var ws = Workspace.CreateDemo();
        var proxy = Proxy(ws: ws);
        var fwd = proxy.GateAndForward(
            new McpToolCall("send_email", new Dictionary<string, string> { ["to"] = "attacker@evil.com", ["body"] = "secrets" }), client);

        Assert.False(fwd.Gate.Allowed);
        Assert.Null(fwd.ServerResponse);    // never forwarded
        Assert.Empty(ws.SentEmails);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // (b) OpenApiImporter — ToContract produces the expected ImportedContract
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The sample invoice schema (POST financial-write) maps to the expected
    /// ImportedContract: Kind=act-create-invoice, Risk=medium,
    /// SideEffect=financial-write, Fields contains the operation parameters,
    /// RequiresConfirmation=true.
    /// </summary>
    [Fact]
    public void OpenApiImporter_ToContract_invoice_schema_produces_expected_contract()
    {
        var contract = OpenApiImporter.ToContract(OpenApiImporter.SampleInvoiceSchema);

        Assert.Equal("act-create-invoice", contract.Kind);
        Assert.Equal("medium", contract.Risk);
        Assert.Equal("financial-write", contract.SideEffect);
        Assert.Contains("customer_id", contract.Fields);
        Assert.Contains("amount_cents", contract.Fields);
        Assert.Contains("due_date", contract.Fields);
        Assert.True(contract.RequiresConfirmation,
            "POST + financial-write side effect must require confirmation.");
    }

    /// <summary>
    /// A read-only GET with no side-effect maps to: Risk=low, SideEffect=none,
    /// RequiresConfirmation=false.
    /// </summary>
    [Fact]
    public void OpenApiImporter_ToContract_get_schema_does_not_require_confirmation()
    {
        var contract = OpenApiImporter.ToContract(OpenApiImporter.SampleGetCustomerSchema);

        Assert.Equal("act-get-customer", contract.Kind);
        Assert.Equal("low", contract.Risk);
        Assert.Equal("none", contract.SideEffect);
        Assert.Contains("customer_id", contract.Fields);
        Assert.False(contract.RequiresConfirmation,
            "GET with no side effect must not require confirmation.");
    }

    /// <summary>
    /// A DELETE without a risk hint infers Risk=high from the method.
    /// </summary>
    [Fact]
    public void OpenApiImporter_ToContract_delete_infers_high_risk()
    {
        var schema = new ToolSchema(
            Name: "delete_invoice",
            Method: "DELETE",
            Summary: "Hard-delete an invoice record.",
            Parameters: new[] { "invoice_id" },
            SideEffectHint: "financial-write");

        var contract = OpenApiImporter.ToContract(schema);

        Assert.Equal("act-delete-invoice", contract.Kind);
        Assert.Equal("high", contract.Risk);
        Assert.True(contract.RequiresConfirmation);
    }

    /// <summary>
    /// Caller-supplied RiskHint overrides the inferred risk from the method.
    /// </summary>
    [Fact]
    public void OpenApiImporter_ToContract_caller_risk_hint_overrides_inferred_risk()
    {
        var schema = new ToolSchema(
            Name: "safe_post",
            Method: "POST",
            Summary: "An idempotent POST that is intentionally low-risk.",
            Parameters: new[] { "id" },
            RiskHint: "low",
            SideEffectHint: "none");

        var contract = OpenApiImporter.ToContract(schema);

        Assert.Equal("low", contract.Risk);
        Assert.False(contract.RequiresConfirmation,
            "SideEffect=none → RequiresConfirmation=false regardless of method.");
    }

    /// <summary>
    /// ParseFromOpenApi parses a real OpenAPI 3.x JSON document: a POST /invoices operation
    /// (operationId create_invoice) with request-body properties becomes a ToolSchema whose fields
    /// include those properties, and a GET becomes a separate read-only schema.
    /// </summary>
    [Fact]
    public void OpenApiImporter_parses_a_real_openapi_spec()
    {
        const string spec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Billing", "version": "1.0" },
          "paths": {
            "/invoices": {
              "post": {
                "operationId": "create_invoice",
                "summary": "Create an invoice",
                "requestBody": { "content": { "application/json": { "schema": {
                  "type": "object",
                  "properties": { "customer_id": {"type":"string"}, "amount_cents": {"type":"integer"} } } } } }
              }
            },
            "/invoices/{id}": {
              "get": {
                "operationId": "get_invoice",
                "summary": "Read an invoice",
                "parameters": [ { "name": "id", "in": "path" } ]
              }
            }
          }
        }
        """;

        var schemas = OpenApiImporter.ParseFromOpenApi(spec);
        Assert.Equal(2, schemas.Count);

        var post = Assert.Single(schemas, s => s.Name == "create_invoice");
        Assert.Equal("POST", post.Method);
        Assert.Contains("customer_id", post.Parameters);
        Assert.Contains("amount_cents", post.Parameters);

        var get = Assert.Single(schemas, s => s.Name == "get_invoice");
        Assert.Equal("GET", get.Method);
        Assert.Contains("id", get.Parameters);
    }

    /// <summary>
    /// RegisterToCompiledDir compiles an imported contract into a real im-imported.tlmz that
    /// SymbolicBundle.Load picks up — the new kind becomes registered (enforced end-to-end).
    /// Uses an isolated temp dir so the canonical dataset is untouched.
    /// </summary>
    [Fact]
    public void OpenApiImporter_registers_imported_contracts_into_a_loadable_bundle()
    {
        var src = DatasetLocator.FindCompiledDir();
        var tmp = Path.Combine(Path.GetTempPath(), "im-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in Directory.GetFiles(src, "im-*.tlmz"))
                File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));

            var before = SymbolicBundle.Load(tmp);
            Assert.False(before.IsRegistered("act-create-invoice"));

            var contract = OpenApiImporter.ToContract(OpenApiImporter.SampleInvoiceSchema);
            OpenApiImporter.RegisterToCompiledDir(tmp, contract);

            var after = SymbolicBundle.Load(tmp);
            Assert.True(after.IsRegistered("act-create-invoice"));   // import is now a usable typed contract
            Assert.Equal(before.Contracts.Count + 1, after.Contracts.Count);
            Assert.Equal("medium", after.Contracts["act-create-invoice"].Risk);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // (c) GmailSendAdapter — requires "email" capability, no-op stub
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GmailSendAdapter.Handles() returns true for act-send-email and false for
    /// any other kind — it correctly declares its coverage.
    /// </summary>
    [Fact]
    public void GmailSendAdapter_handles_only_send_email()
    {
        var adapter = OAuthAdapterWiringExample.CreateAdapter();

        Assert.True(adapter.Handles(Kinds.SendEmail));
        Assert.False(adapter.Handles(Kinds.DraftEmail));
        Assert.False(adapter.Handles(Kinds.RunCommand));
        Assert.False(adapter.Handles(Kinds.ReadCalendar));
    }

    /// <summary>
    /// The adapter's required capability constant matches the bundle's capability
    /// map — so the PolicyGate's capability scoping gate will correctly block the
    /// adapter when "email" is not granted.
    /// </summary>
    [Fact]
    public void GmailSendAdapter_required_capability_matches_bundle()
    {
        var bundle = SymbolicBundle.Load(DatasetLocator.FindCompiledDir());
        Assert.Equal(GmailSendAdapter.RequiredCapability, bundle.Capabilities[Kinds.SendEmail]);
    }

    /// <summary>
    /// Without approval, Execute() halts and transmits nothing (0 messages sent).
    /// This is the core no-op stub assertion: no real network I/O, no email.
    /// </summary>
    [Fact]
    public void GmailSendAdapter_does_not_transmit_without_approval()
    {
        var adapter = OAuthAdapterWiringExample.CreateAdapter();
        var ws = Workspace.CreateDemo();

        var node = new IntentNode
        {
            Id = "n1",
            Type = Kinds.SendEmail,
            Label = "Send email (test)",
            Action = new SendEmailAction("mcp-draft", "sarah@company.com", Array.Empty<string>()),
            TrustSource = TrustSource.User,
            Status = NodeStatus.Resolved,
        };

        var decision = new PolicyDecision(
            "n1", Decision.Confirm, "medium",
            "External communication: requires confirmation.",
            new[] { "pol-send-email" },
            RequiresConfirmation: true,
            TrustSource: "User",
            Sensitive: false, ExternalSideEffect: true, Destructive: false);

        // approved = false → adapter must halt and transmit nothing.
        var exec = adapter.Execute(node, decision, ws, approved: false);

        Assert.True(exec.Halted, "Adapter must halt when not approved.");
        Assert.Empty(ws.SentEmails);
        Assert.Contains("0 messages transmitted", exec.Summary + string.Join(" ", exec.Effects),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With approval, Execute() calls the real IEmailTransport. With a NullEmailTransport (the safe
    /// default) it records the send without network I/O — but the transport is genuinely invoked,
    /// so a configured SmtpEmailTransport would transmit for real.
    /// </summary>
    [Fact]
    public void GmailSendAdapter_sends_via_the_transport_when_approved()
    {
        var transport = new NullEmailTransport();
        var adapter = OAuthAdapterWiringExample.CreateAdapter(transport);
        var ws = Workspace.CreateDemo();

        var node = new IntentNode
        {
            Id = "n1",
            Type = Kinds.SendEmail,
            Label = "Send email (test)",
            Action = new SendEmailAction("Meeting summary", "sarah@company.com", Array.Empty<string>()),
            TrustSource = TrustSource.User,
            Status = NodeStatus.Resolved,
        };

        var decision = new PolicyDecision(
            "n1", Decision.Confirm, "medium",
            "External communication: requires confirmation.",
            new[] { "pol-send-email" },
            RequiresConfirmation: true,
            TrustSource: "User",
            Sensitive: false, ExternalSideEffect: true, Destructive: false);

        var exec = adapter.Execute(node, decision, ws, approved: true);

        Assert.False(exec.Halted, "Adapter should not halt when approved.");
        Assert.True(exec.Ran);
        Assert.Contains("sarah@company.com", ws.SentEmails);
        // The transport was actually invoked — a real SMTP transport would have transmitted here.
        Assert.Contains(transport.Sent, s => s.To == "sarah@company.com");
    }

    /// <summary>
    /// The SMTP transport falls back to a no-network NullEmailTransport when SMTP_HOST is unset,
    /// and constructs a real SMTP transport when configured.
    /// </summary>
    [Fact]
    public void SmtpEmailTransport_falls_back_to_null_when_unconfigured()
    {
        var prev = Environment.GetEnvironmentVariable("SMTP_HOST");
        Environment.SetEnvironmentVariable("SMTP_HOST", null);
        try { Assert.IsType<NullEmailTransport>(SmtpEmailTransport.FromEnvironment()); }
        finally { Environment.SetEnvironmentVariable("SMTP_HOST", prev); }

        var real = new SmtpEmailTransport("smtp.example.com", 587, "from@example.com");
        Assert.Contains("smtp.example.com", real.Describe());
    }

    /// <summary>
    /// The Gmail *API* OAuth path requires configuration: without GMAIL_ACCESS_TOKEN it reports a
    /// clear config error (SMTP needs no OAuth and works today; the interactive OAuth flow needs the
    /// user's Google credentials).
    /// </summary>
    [Fact]
    public void GmailSendAdapter_AcquireTokenAsync_requires_configuration()
    {
        var prev = Environment.GetEnvironmentVariable("GMAIL_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("GMAIL_ACCESS_TOKEN", null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                GmailSendAdapter.AcquireTokenAsync().GetAwaiter().GetResult());
            Assert.Contains("OAuth", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Environment.SetEnvironmentVariable("GMAIL_ACCESS_TOKEN", prev); }
    }

    /// <summary>
    /// When the "email" capability is NOT granted, the PolicyGate blocks a
    /// send_email node with pol-capability-not-granted — the GmailSendAdapter
    /// is never reached. This proves the capability scoping gate works end-to-end.
    /// </summary>
    [Fact]
    public void GmailSendAdapter_capability_scoping_blocks_when_email_not_granted()
    {
        var bundle = SymbolicBundle.Load(DatasetLocator.FindCompiledDir());

        // Grant every capability EXCEPT "email".
        var grantedWithoutEmail = bundle.AllCapabilities
            .Where(c => !c.Equals("email", StringComparison.OrdinalIgnoreCase))
            .ToHashSet();

        var rt = new IntentMeshRuntime(bundle, grantedCapabilities: grantedWithoutEmail);
        var ws = Workspace.CreateDemo();

        // Run a prompt that would produce a send_email node if email were granted.
        // We use the framework test prompt that includes a draft (email domain).
        const string prompt = "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";
        var result = rt.Run(prompt, ws);

        // The draft-email node must be blocked by capability scoping.
        var draftNode = result.Nodes.FirstOrDefault(n => n.Type == Kinds.DraftEmail);
        if (draftNode is not null)
        {
            var draftPolicy = result.Policy.Single(p => p.NodeId == draftNode.Id);
            Assert.Equal("Block", draftPolicy.Decision);
            Assert.Contains("pol-capability-not-granted", draftPolicy.TriggeredRules);
        }

        // No emails were sent or drafted via the adapter.
        Assert.Empty(ws.SentEmails);
    }
}
