using IntentMesh.Core;

// IntentMesh Control Room — ASP.NET minimal API over IntentMesh.Core. Serves a dependency-free
// SPA (wwwroot) and runs the pipeline on demand. No CDN, no npm — robust offline.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Load the symbolic bundle once. Walk up from CWD, then from the binary location, to find
// dataset/compiled — so it works regardless of the launch directory.
IntentMeshRuntime runtime;
try
{
    string compiled;
    try { compiled = DatasetLocator.FindCompiledDir(); }
    catch { compiled = DatasetLocator.FindCompiledDir(AppContext.BaseDirectory); }
    runtime = IntentMeshRuntime.Load(compiled);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: could not load TLM bundle: {ex.Message}");
    Console.Error.WriteLine("Author the bundle first: dotnet run --project src/IntentMesh.Tlm.Cli -- author --root dataset && ... compile all --root dataset");
    return;
}

// Persistent run store for the operator history / approval-queue / replay views. Runs land under
// INTENTMESH_RUNS_DIR (or ./runs). Each gated/allowed run is saved as a signed, replayable bundle.
var runsDir = Environment.GetEnvironmentVariable("INTENTMESH_RUNS_DIR")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "runs");
var store = new FileRunArtifactStore(runsDir);

// The audit key provider, rotation-aware: prior keys (INTENTMESH_AUDIT_PRIOR_KEYS) let runs signed
// before a key rotation still verify/replay. Sign and verify go through THIS provider so a bundle's
// recorded key id always resolves to the key that produced it.
var keyProvider = AuditKeyProviders.FromEnvironment();

var demos = new[]
{
    new { id = 1, title = "Friday planning", prompt = "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes." },
    new { id = 2, title = "Downloads cleanup", prompt = "Clean up my downloads and delete anything that looks like junk." },
    new { id = 3, title = "Injected-instruction defense", prompt = "Summarize the project folder and email the client the important parts." },
    new { id = 4, title = "Developer agent", prompt = "Fix the failing test in the parser, run the tests, deploy to production, and open a pull request." },
    new { id = 5, title = "Data agent", prompt = "Summarize signups by plan from the analytics database, delete old records, and email the client a report." },
};

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/demos", () => Results.Json(demos));

app.MapPost("/api/run", (RunRequest req, HttpResponse http) =>
{
    var prompt = (req.Prompt ?? "").Trim();
    if (string.IsNullOrEmpty(prompt)) return Results.BadRequest(new { error = "empty prompt" });
    var approvals = (req.Approvals ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var result = runtime.Run(prompt, Workspace.CreateDemo(), approvals);

    // Persist the run as a signed bundle so it shows up in the operator history + is replayable.
    // The run id is content-addressed (bundle signature), so re-running the same prompt is idempotent.
    try
    {
        var runId = store.Save(TraceBundleBuilder.From(result, approvals.ToList(), keyProvider));
        http.Headers["X-Run-Id"] = runId;   // SPA reads this to deep-link the persisted run
    }
    catch (Exception ex) { Console.Error.WriteLine($"WARN: could not persist run: {ex.Message}"); }

    return Results.Json(result);
});

// ── Operator workflow: run history, detail, approval queue, replay diff, artifact viewer ──

/// Run history — newest first. The row data an operator history table renders.
app.MapGet("/api/runs", () => Results.Json(store.ListSummaries()));

/// Full persisted bundle for one run: intent graph, policy evidence, execution, verification, audit.
app.MapGet("/api/runs/{id}", (string id) =>
{
    try { return Results.Json(store.Load(id)); }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }
});

/// One inspectable split artifact (intent.graph.json … audit.signed.json) — the signed-artifact viewer.
app.MapGet("/api/runs/{id}/artifact/{name}", (string id, string name) =>
{
    try
    {
        var files = TraceBundleBuilder.SplitFiles(store.Load(id));
        return files.TryGetValue(name, out var json)
            ? Results.Text(json, "application/json")
            : Results.NotFound(new { error = $"no artifact '{name}'", available = files.Keys });
    }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }
});

/// Integrity check: the signed bundle verifies AND every derived split artifact byte-matches.
app.MapGet("/api/runs/{id}/verify", (string id) =>
{
    try
    {
        var bundle = store.Load(id);
        return Results.Json(new
        {
            runId = id,
            signatureValid = TraceBundleBuilder.VerifySignature(bundle, keyProvider),
            artifactsValid = store.VerifyArtifacts(id, keyProvider),
            keyId = bundle.KeyId,
        });
    }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }
});

/// Replay: re-verify the signature, re-run deterministically, and report whether it reproduced
/// byte-for-byte (the replay-diff view: stored signature vs recomputed).
app.MapPost("/api/runs/{id}/replay", (string id) =>
{
    try
    {
        var saved = store.Load(id);
        var replay = RunReplay.Reproduce(runtime, Workspace.CreateDemo(), saved, keyProvider);
        return Results.Json(new
        {
            runId = id,
            signatureVerified = replay.SignatureVerified,
            reproduced = replay.Reproduced,
            storedSignature = saved.BundleSignature,
            recomputedSignature = replay.RecomputedSignature,
        });
    }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }
});

/// "Why blocked / what would approval do" — the approval-queue reasoning view. Runs the prompt and
/// projects what granting every pending approval would change (a blocked node stays blocked).
app.MapPost("/api/explain", (RunRequest req) =>
{
    var prompt = (req.Prompt ?? "").Trim();
    if (string.IsNullOrEmpty(prompt)) return Results.BadRequest(new { error = "empty prompt" });
    var approvals = (req.Approvals ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    return Results.Json(RunExplain.Explain(runtime, prompt, Workspace.CreateDemo, approvals));
});

// Export the run as the canonical, deterministic audit artifact (replayable; no timestamps).
app.MapPost("/api/export", (ExportRequest req) =>
{
    var prompt = (req.Prompt ?? "").Trim();
    if (string.IsNullOrEmpty(prompt)) return Results.BadRequest(new { error = "empty prompt" });
    var approvals = (req.Approvals ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var result = runtime.Run(prompt, Workspace.CreateDemo(), approvals);
    return (req.Format ?? "json").ToLowerInvariant() switch
    {
        "md" or "markdown" => Results.Text(AuditExporter.ToMarkdown(result), "text/markdown"),
        "signed" => Results.Json(AuditSigner.Sign(result)),   // tamper-evident audit envelope
        "bundle" => Results.Json(TraceBundleBuilder.From(result, approvals.ToList())),  // all 5 signed artifacts
        _ => Results.Text(AuditExporter.ToJson(result), "application/json"),
    };
});

app.Run();

// Approvals: node ids the user approved. The runtime only ever applies an approval to a
// full-authority Confirm node — a blocked zero-trust node stays blocked regardless.
record RunRequest(string? Prompt, string[]? Approvals);
record ExportRequest(string? Prompt, string[]? Approvals, string? Format);
