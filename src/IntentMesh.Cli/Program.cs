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

// export: run a prompt and write the signed trace bundle (all five artifacts).
//   intentmesh export "<prompt>" [--out bundle.json] [--split <dir>]
if (rest[0] == "export")
{
    string? outPath = null, splitDir = null;
    var words = new List<string>();
    for (int i = 1; i < rest.Count; i++)
    {
        if (rest[i] == "--out" && i + 1 < rest.Count) { outPath = rest[++i]; continue; }
        if (rest[i] == "--split" && i + 1 < rest.Count) { splitDir = rest[++i]; continue; }
        words.Add(rest[i]);
    }
    var prompt = string.Join(" ", words);
    if (string.IsNullOrWhiteSpace(prompt)) { Console.Error.WriteLine("usage: intentmesh export \"<request>\" [--out file] [--split dir]"); return 1; }
    var bundle = TraceBundleBuilder.From(runtime.Run(prompt, Workspace.CreateDemo()));
    outPath ??= "intentmesh-bundle.json";
    File.WriteAllText(outPath, TraceBundleBuilder.ToJson(bundle));
    Console.WriteLine($"wrote {outPath}  (bundle signature {bundle.BundleSignature[..16]}…)");
    if (splitDir is not null)
    {
        Directory.CreateDirectory(splitDir);
        foreach (var (name, json) in TraceBundleBuilder.SplitFiles(bundle))
            File.WriteAllText(Path.Combine(splitDir, name), json);
        Console.WriteLine($"wrote 5 artifacts to {splitDir}/");
    }
    return 0;
}

// replay: reload a bundle, re-run its prompt, and assert the bundle is byte-identical (deterministic).
//   intentmesh replay <bundle.json>
if (rest[0] == "replay")
{
    if (rest.Count < 2) { Console.Error.WriteLine("usage: intentmesh replay <bundle.json>"); return 1; }
    if (!File.Exists(rest[1])) { Console.Error.WriteLine($"no such file: {rest[1]}"); return 1; }
    var original = TraceBundleBuilder.FromJson(File.ReadAllText(rest[1]));
    bool sigOk = TraceBundleBuilder.VerifySignature(original);
    var approvals = original.Approvals.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var rerun = TraceBundleBuilder.From(runtime.Run(original.Prompt, Workspace.CreateDemo(), approvals), original.Approvals);
    bool identical = rerun.BundleSignature == original.BundleSignature;
    Console.WriteLine($"prompt:    \"{original.Prompt}\"");
    Console.WriteLine($"signature: {(sigOk ? "VALID (untampered)" : "INVALID (tampered!)")}");
    Console.WriteLine($"replay:    {(identical ? "DETERMINISTIC — re-run is byte-identical" : "MISMATCH — re-run differs!")}");
    Console.WriteLine($"  original {original.BundleSignature[..16]}…  rerun {rerun.BundleSignature[..16]}…");
    return sigOk && identical ? 0 : 1;
}

// verify-run: full tamper-verification of a PERSISTED run in a store directory — checks the signed
// bundle + every derived split artifact, then replays for byte-identical reproduction.
//   intentmesh verify-run <runsDir> <runId>
if (rest[0] == "verify-run")
{
    if (rest.Count < 3) { Console.Error.WriteLine("usage: intentmesh verify-run <runsDir> <runId>"); return 1; }
    var store = new FileRunArtifactStore(rest[1]);
    TraceBundle bundle;
    try { bundle = store.Load(rest[2]); }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    bool artifactsOk = store.VerifyArtifacts(rest[2]);
    var replay = RunReplay.Reproduce(runtime, Workspace.CreateDemo(), bundle);
    Console.WriteLine($"run:       {rest[2]}  (key {bundle.SignedAudit.KeyId})");
    Console.WriteLine($"prompt:    \"{bundle.Prompt}\"");
    Console.WriteLine($"artifacts: {(artifactsOk ? "INTACT (bundle + 5 split files match the signature)" : "TAMPERED")}");
    Console.WriteLine($"signature: {(replay.SignatureVerified ? "VALID" : "INVALID")}");
    Console.WriteLine($"replay:    {(replay.Reproduced ? "DETERMINISTIC — byte-identical" : "MISMATCH")}");
    return artifactsOk && replay.SignatureVerified && replay.Reproduced ? 0 : 1;
}

// bare prompt
Trace.Render(runtime.Run(string.Join(" ", rest), Workspace.CreateDemo()), "trace");
return 0;
