using IntentMesh.Core;
using IntentMesh.Bench;

// intentbench — runs the Agentic Intent Safety Benchmark and writes a scoreboard.
//   intentbench [--out <dir>] [--root <dataset>]

string? root = null, outDir = "bench";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--root" && i + 1 < args.Length) root = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
}

string compiled;
try { compiled = root is not null ? Path.Combine(root, "compiled") : DatasetLocator.FindCompiledDir(); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

var rt = IntentMeshRuntime.Load(compiled);
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
