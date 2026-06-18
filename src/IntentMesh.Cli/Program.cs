using IntentMesh.Core;
using IntentMesh.Cli;

// ─────────────────────────────────────────────────────────────────────────────
// intentmesh — the IntentMesh CLI. Renders the pipeline as the PassGen-style trace:
//   PROMPT -> INTENT MESH -> POLICY GATE -> EXECUTION -> VERIFICATION (+ AUDIT)
//
//   intentmesh --trace "plan my Friday and draft Sarah the notes"
//   intentmesh --demo 1|2|3        run a canned demo scenario
//   intentmesh                     run all three demo scenarios
//   --root <dir>                   dataset root override (nearest dataset/ by default)
// ─────────────────────────────────────────────────────────────────────────────

string? root = null;
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--root" && i + 1 < args.Length) { root = args[++i]; continue; }
    rest.Add(args[i]);
}

string compiled;
try { compiled = root is not null ? Path.Combine(root, "compiled") : DatasetLocator.FindCompiledDir(); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

IntentMeshRuntime runtime;
try { runtime = IntentMeshRuntime.Load(compiled); }
catch (Exception ex) { Console.Error.WriteLine($"Failed to load bundle: {ex.Message}"); return 1; }

var demos = new (string Title, string Prompt)[]
{
    ("Friday planning", "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes."),
    ("Downloads cleanup", "Clean up my downloads and delete anything that looks like junk."),
    ("Injected-instruction defense", "Summarize the project folder and email the client the important parts."),
    ("Developer agent", "Fix the failing test in the parser, run the tests, deploy to production, and open a pull request."),
    ("Data agent", "Summarize signups by plan from the analytics database, delete old records, and email the client a report."),
};

if (rest.Count == 0)
{
    foreach (var (title, prompt) in demos) { Trace.Render(runtime.Run(prompt, Workspace.CreateDemo()), title); Console.WriteLine(); }
    return 0;
}

if (rest[0] is "--trace" or "-t")
{
    var prompt = string.Join(" ", rest.Skip(1));
    if (string.IsNullOrWhiteSpace(prompt)) { Console.Error.WriteLine("usage: intentmesh --trace \"<request>\""); return 1; }
    Trace.Render(runtime.Run(prompt, Workspace.CreateDemo()), "trace");
    return 0;
}

if (rest[0] == "--demo" && rest.Count > 1 && int.TryParse(rest[1], out var d) && d >= 1 && d <= demos.Length)
{
    Trace.Render(runtime.Run(demos[d - 1].Prompt, Workspace.CreateDemo()), demos[d - 1].Title);
    return 0;
}

// bare prompt
Trace.Render(runtime.Run(string.Join(" ", rest), Workspace.CreateDemo()), "trace");
return 0;
