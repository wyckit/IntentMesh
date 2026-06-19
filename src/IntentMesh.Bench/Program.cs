using IntentMesh.Core;
using IntentMesh.Bench;
using IntentMesh.Integrations;

// intentbench — runs the Agentic Intent Safety Benchmark and writes a scoreboard.
//   intentbench [--out <dir>] [--root <dataset>] [--live]
//
// The IntentMesh column always runs the REAL pipeline (gate/verify/audit). By default the proposal
// layer is the deterministic rule-based resolver (so the benchmark is reproducible). With --live (and
// ANTHROPIC_API_KEY set) the proposal layer is a real LLM (LlmIntentProposer) — a live head-to-head;
// it's still fully gated. The two baselines remain deterministic architecture-class models.

string? root = null, outDir = "bench";
bool live = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--root" && i + 1 < args.Length) root = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--live") live = true;
}

string compiled;
try { compiled = root is not null ? Path.Combine(root, "compiled") : DatasetLocator.FindCompiledDir(); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

var bundle = SymbolicBundle.Load(compiled);
string proposerLabel = "rule-based resolver (deterministic)";
IIntentProposer? proposer = null;
if (live)
{
    var llm = AnthropicLlmClient.FromEnvironment();
    if (llm is null)
        Console.Error.WriteLine("--live requested but ANTHROPIC_API_KEY is unset; falling back to the deterministic resolver.");
    else { proposer = new LlmIntentProposer(bundle, llm); proposerLabel = "LlmIntentProposer (live model)"; }
}
var rt = new IntentMeshRuntime(bundle, proposer);
Console.WriteLine($"IntentMesh proposal layer: {proposerLabel}\n");
var rows = BenchRunner.Run(rt);

Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir, "REPORT.md"), Report.Markdown(rows));
File.WriteAllText(Path.Combine(outDir, "scoreboard.html"), Report.Html(rows));

Console.WriteLine(Report.ConsoleSummary(rows));
int meshPerfect = rows.Count(r => r.Mesh.Total == 4);
Console.WriteLine();
Console.WriteLine($"IntentMesh perfect (4/4) on {meshPerfect}/{rows.Count} scenarios.");
Console.WriteLine($"Report -> {Path.Combine(outDir, "REPORT.md")} · scoreboard -> {Path.Combine(outDir, "scoreboard.html")}");
return meshPerfect == rows.Count ? 0 : 1;
