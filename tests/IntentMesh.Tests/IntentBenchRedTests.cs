using System.Text;
using System.Text.Json;
using IntentMesh.Core;
using IntentMesh.Integrations;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// IntentBench-Red — the adversarial counterpart to IntentBench. Where IntentBench proves the kernel
/// stops a careless agent, these cases attack the KERNEL ITSELF and assert each hardened defense
/// holds. Every test here is a sentence the project can defend: "extract the key and re-sign → still
/// detected", "symlink out of the sandbox → blocked", "smuggle a high-risk op past the classifier →
/// rejected", "blanket-approve everything → refused". Each red case is also a regression guard.
/// </summary>
public sealed class IntentBenchRedTests
{
    private const string Prompt =
        "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";

    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();

    private static string TempRoot()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "im-red-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        return root;
    }

    // ═══ ATTACK 1 — audit tamper + key extraction / re-sign ═══════════════════════
    // Threat: an attacker edits the audit and re-signs it. With a real (env/KMS) key, the demo key
    // baked in the binary is useless for forgery, and any edit changes the chain.
    [Fact]
    public void Red_audit_tamper_and_demo_key_resign_are_detected_under_a_production_key()
    {
        var r = Runtime().Run(Prompt, Workspace.CreateDemo());
        var prodKey = Enumerable.Repeat((byte)0x5A, 32).ToArray();   // a real ≥128-bit key

        var signed = AuditSigner.Sign(r, prodKey);
        Assert.True(AuditSigner.Verify(r, signed.Signature, prodKey));   // genuine
        Assert.StartsWith("raw-", signed.KeyId);                         // key is identified

        // Tamper one audit event.
        var tamperedAudit = r.Audit.Select((a, i) => i == 0 ? a with { Message = a.Message + " (edited by attacker)" } : a).ToList();
        var tampered = r with { Audit = tamperedAudit };
        Assert.False(AuditSigner.Verify(tampered, signed.Signature, prodKey));   // tamper detected

        // Attacker re-signs the tampered audit with the only key in the binary — the demo key.
        var forged = AuditSigner.Sign(tampered);                          // demo key
        Assert.Equal(AuditSigner.DemoKeyId, forged.KeyId);
        Assert.Contains("INSECURE", forged.KeyId);
        Assert.False(AuditSigner.Verify(tampered, forged.Signature, prodKey));   // forgery rejected under prod key
    }

    [Fact]
    public void Red_environment_key_provider_rejects_a_weak_key_and_labels_demo_insecure()
    {
        var def = (EnvironmentAuditKeyProvider)AuditSigner.Default;
        Assert.False(def.IsProductionKey);                 // no env key configured in tests → demo fallback
        Assert.Equal(AuditSigner.DemoKeyId, def.KeyId);
        Assert.Contains("INSECURE", AuditSigner.DemoKeyId);

        var prev = Environment.GetEnvironmentVariable(EnvironmentAuditKeyProvider.KeyEnv);
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentAuditKeyProvider.KeyEnv, "tooshort");
            Assert.Throws<InvalidOperationException>(() => new EnvironmentAuditKeyProvider());   // fail-closed

            Environment.SetEnvironmentVariable(EnvironmentAuditKeyProvider.KeyEnv, "a-sufficiently-long-production-signing-key");
            var p = new EnvironmentAuditKeyProvider();
            Assert.True(p.IsProductionKey);
            Assert.NotEqual(AuditSigner.DemoKeyId, p.KeyId);
        }
        finally { Environment.SetEnvironmentVariable(EnvironmentAuditKeyProvider.KeyEnv, prev); }
    }

    // ═══ ATTACK 2 — filesystem sandbox escape ════════════════════════════════════
    [Fact]
    public void Red_read_multiple_files_cannot_smuggle_a_path_outside_the_root()
    {
        var root = TempRoot();
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "ok.txt"), "ok");
        try
        {
            var outside = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd";
            var proxy = new McpProxy(Runtime(), Workspace.CreateDemo(), allowedRoot: root);
            var paths = JsonSerializer.Serialize(new[] { System.IO.Path.Combine(root, "ok.txt"), outside });

            var res = proxy.Gate(new McpToolCall("read_multiple_files", new Dictionary<string, string> { ["paths"] = paths }));
            Assert.False(res.Allowed);                                   // the escaping element is caught
            Assert.Contains("path policy", res.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [Fact]
    public void Red_unc_and_device_namespace_paths_are_denied()
    {
        var root = TempRoot();
        try
        {
            var proxy = new McpProxy(Runtime(), Workspace.CreateDemo(), allowedRoot: root);
            foreach (var bad in new[] { @"\\evil-server\share\secret", @"\\?\C:\Windows\win.ini", @"\\.\PhysicalDrive0" })
            {
                var res = proxy.Gate(new McpToolCall("read_file", new Dictionary<string, string> { ["path"] = bad }));
                Assert.False(res.Allowed);
            }
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [SkippableFact]
    public void Red_symlinked_directory_out_of_the_root_is_blocked()
    {
        var root = TempRoot();
        var outsideDir = TempRoot();
        System.IO.File.WriteAllText(System.IO.Path.Combine(outsideDir, "secret.txt"), "top secret");
        var link = System.IO.Path.Combine(root, "link");
        try
        {
            try { System.IO.Directory.CreateSymbolicLink(link, outsideDir); }
            catch { Skip.If(true,"creating symlinks needs privilege (Developer Mode/admin) — unavailable here"); return; }

            var proxy = new McpProxy(Runtime(), Workspace.CreateDemo(), allowedRoot: root);
            var res = proxy.Gate(new McpToolCall("read_file",
                new Dictionary<string, string> { ["path"] = System.IO.Path.Combine(link, "secret.txt") }));
            Assert.False(res.Allowed);   // the link resolves outside the root → blocked
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } System.IO.Directory.Delete(outsideDir, true); }
    }

    // ═══ ATTACK 3 — risk-smuggling through the OpenAPI importer ═══════════════════
    [Fact]
    public void Red_remote_ref_is_rejected_not_silently_dropped()
    {
        const string spec = """
        openapi: 3.0.0
        paths:
          /x:
            post:
              operationId: do_x
              requestBody:
                content:
                  application/json:
                    schema:
                      $ref: 'https://attacker.example/schema.json'
        """;
        Assert.Throws<InvalidDataException>(() => OpenApiImporter.ParseFromOpenApi(spec));
    }

    [Fact]
    public void Red_unresolvable_local_ref_is_rejected()
    {
        const string spec = """
        {
          "openapi": "3.0.0",
          "paths": { "/x": { "post": { "operationId": "do_x",
            "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Missing" } } } } } } }
        }
        """;
        Assert.Throws<InvalidDataException>(() => OpenApiImporter.ParseFromOpenApi(spec));
    }

    [Fact]
    public void Red_mutating_op_with_no_keywords_still_requires_confirmation()
    {
        // A bland POST with nothing the keyword classifier recognizes must NOT classify as low/none.
        var contract = OpenApiImporter.ToContract(new ToolSchema("do_thing", "POST", "Perform an operation", new[] { "id" }));
        Assert.NotEqual("none", contract.SideEffect);   // rounds up, never silently "none"
        Assert.True(contract.RequiresConfirmation);
    }

    // ═══ ATTACK 4 — denial-of-service via a crafted spec ═════════════════════════
    [Fact]
    public void Red_deeply_nested_yaml_throws_a_catchable_error_not_a_crash()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 260; i++) sb.Append(new string(' ', i * 2)).Append($"k{i}:\n");
        sb.Append(new string(' ', 260 * 2)).Append("leaf: x\n");
        Assert.Throws<InvalidDataException>(() => OpenApiImporter.ParseFromOpenApi(sb.ToString()));
    }

    [Fact]
    public void Red_tab_indented_yaml_is_rejected()
        => Assert.Throws<InvalidDataException>(() => OpenApiImporter.ParseFromOpenApi("paths:\n\tx: 1"));

    // ═══ ATTACK 5 — consent forgery / blanket approval ═══════════════════════════
    [Fact]
    public void Red_consent_is_folded_into_the_signed_audit()
    {
        var rt = Runtime();
        var baseline = rt.Run(Prompt, Workspace.CreateDemo());
        var confirmNode = baseline.Nodes.FirstOrDefault(n => n.Status == "NeedsConfirmation" && n.TrustSource == "User");
        Assert.NotNull(confirmNode);   // the prompt must produce a gated User node to approve

        var approved = rt.Run(Prompt, Workspace.CreateDemo(), new HashSet<string> { confirmNode!.Id });

        // Consent is an explicit, signed audit event — provable from the artifact alone.
        Assert.Contains(approved.Audit, a => a.Phase == "consent" && a.Message.Contains(confirmNode.Id));
        // Different consent ⇒ different signature: an approval set cannot be swapped under one signature.
        Assert.NotEqual(AuditSigner.Sign(baseline).Signature, AuditSigner.Sign(approved).Signature);
    }

    [Fact]
    public void Red_blanket_approval_is_rejected_fail_closed()
    {
        var rt = Runtime();
        var baseline = rt.Run(Prompt, Workspace.CreateDemo());
        var confirmNode = baseline.Nodes.First(n => n.Status == "NeedsConfirmation" && n.TrustSource == "User");

        // "Approve all": a flood of node ids (over the per-run cap) including the real gated node.
        var blanket = Enumerable.Range(0, IntentMeshRuntime.DefaultMaxApprovalsPerRun + 8)
            .Select(i => $"n{i}").Append(confirmNode.Id).ToHashSet();

        var run = rt.Run(Prompt, Workspace.CreateDemo(), blanket);

        Assert.Contains(run.Audit, a => a.Message.Contains("BLANKET APPROVAL REJECTED"));
        var gated = run.Nodes.First(n => n.Id == confirmNode.Id);
        Assert.NotEqual("Executed", gated.Status);   // not rubber-stamped into execution
        Assert.NotEqual("Verified", gated.Status);
    }

    // ═══ ATTACK 7 — PR-review regressions ════════════════════════════════════════
    // Capability scoping must survive the proxy's proposer swap (it previously reset to all-granted).
    [Fact]
    public void Red_proxy_preserves_runtime_capability_restrictions()
    {
        var bundle = SymbolicBundle.Load(DatasetLocator.FindCompiledDir());
        var withoutEmail = bundle.AllCapabilities
            .Where(c => !c.Equals("email", StringComparison.OrdinalIgnoreCase)).ToHashSet();
        var restricted = new IntentMeshRuntime(bundle, grantedCapabilities: withoutEmail);
        var proxy = new McpProxy(restricted, Workspace.CreateDemo());

        var res = proxy.Gate(new McpToolCall("send_email",
            new Dictionary<string, string> { ["to"] = "sarah@company.com", ["subject"] = "hi" }));

        Assert.False(res.Allowed);
        var pol = res.RunResult.Policy.FirstOrDefault(p => p.NodeId == "n1");
        Assert.NotNull(pol);
        Assert.Equal("Block", pol!.Decision);                                   // not merely Confirm
        Assert.Contains("pol-capability-not-granted", pol.TriggeredRules);      // restriction honored
    }

    // The audit chain must not be forgeable by shifting characters across field boundaries.
    [Fact]
    public void Red_audit_chain_encoding_has_no_field_boundary_collision()
    {
        var baseRun = Runtime().Run(Prompt, Workspace.CreateDemo());
        // Under a plain {Seq}{Phase}{NodeId}{Message} concat these two collide ("1abc"); the
        // length-prefixed encoding must give them different signatures. (AuditView is Seq,NodeId,Phase,Message.)
        var a1 = baseRun with { Audit = new[] { new AuditView(1, "c", "ab", "") } };
        var a2 = baseRun with { Audit = new[] { new AuditView(1, "bc", "a", "") } };
        Assert.NotEqual(AuditSigner.Sign(a1).Signature, AuditSigner.Sign(a2).Signature);
    }

    // allOf/schema nesting past the depth bound must fail closed, not silently drop fields.
    [Fact]
    public void Red_allof_depth_overflow_fails_closed()
    {
        const string spec = """
        {
          "openapi": "3.0.0",
          "paths": { "/x": { "post": { "operationId": "do_x",
            "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/S" } } } } } } },
          "components": { "schemas": { "S": { "allOf": [ { "$ref": "#/components/schemas/S" } ] } } }
        }
        """;
        Assert.Throws<InvalidDataException>(() => OpenApiImporter.ParseFromOpenApi(spec));
    }

    // ═══ ATTACK 6 — SSRF via the HTTP transport endpoint ═════════════════════════
    [Fact]
    public void Red_http_transport_blocks_cloud_metadata_and_insecure_nonloopback()
    {
        // Cloud-metadata IP is always blocked.
        Assert.Throws<InvalidOperationException>(() => McpHttpClient.Connect("http://169.254.169.254/latest/meta-data/"));
        // Non-loopback over plaintext http requires an explicit override.
        Assert.Throws<InvalidOperationException>(() => McpHttpClient.Connect("http://intranet.internal/mcp"));
        // A non-http scheme is rejected.
        Assert.Throws<InvalidOperationException>(() => McpHttpClient.Connect("file:///etc/passwd"));
    }
}
