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

app.MapPost("/api/run", (RunRequest req) =>
{
    var prompt = (req.Prompt ?? "").Trim();
    if (string.IsNullOrEmpty(prompt)) return Results.BadRequest(new { error = "empty prompt" });
    var approvals = (req.Approvals ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    return Results.Json(runtime.Run(prompt, Workspace.CreateDemo(), approvals));
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
