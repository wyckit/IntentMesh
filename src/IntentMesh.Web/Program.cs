using System.Net;
using System.Security.Cryptography;
using System.Text;
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

// Production safety: refuse to start in the Production environment with only the INSECURE demo audit
// key — signed audits would be forgeable by anyone who knows the (public) demo key. Configure a real
// INTENTMESH_AUDIT_KEY (>=128-bit), or set INTENTMESH_ALLOW_INSECURE_KEY=1 to acknowledge a deliberately
// local/non-production host. Auth, rate-limiting, and multi-tenant isolation remain preview-out-of-scope
// (see docs/MATURITY.md) — run the Control Room behind your own boundary, not exposed publicly.
if (app.Environment.IsProduction()
    && keyProvider.KeyId == AuditSigner.DemoKeyId
    && Environment.GetEnvironmentVariable("INTENTMESH_ALLOW_INSECURE_KEY") != "1")
{
    Console.Error.WriteLine(
        "FATAL: refusing to start in Production with the INSECURE demo audit key — signed audits would be forgeable. " +
        "Set INTENTMESH_AUDIT_KEY (>=128-bit; base64 or utf8), or set INTENTMESH_ALLOW_INSECURE_KEY=1 for a deliberately local-only host.");
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

// API access guard: the /api surface (run, history, artifact, replay, export, explain) is LOCAL-ONLY
// by default — a non-loopback caller is refused. Set INTENTMESH_WEB_TOKEN to allow remote access; then
// every /api request must present it (X-Api-Token, or 'Authorization: Bearer <token>'). This stops a
// public caller from enumerating artifacts or using a real-key host as a signing/approval oracle.
// (Full auth / rate-limiting / multi-tenant isolation remain preview-out-of-scope — see docs/MATURITY.md.)
var apiToken = Environment.GetEnvironmentVariable("INTENTMESH_WEB_TOKEN");
const long MaxApiBodyBytes = 256 * 1024;   // prompts/approvals are tiny; reject oversized bodies (basic DoS guard)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (context.Request.ContentLength > MaxApiBodyBytes)
        {
            context.Response.StatusCode = 413;   // Payload Too Large
            await context.Response.WriteAsJsonAsync(new { error = $"request body exceeds {MaxApiBodyBytes} bytes" });
            return;
        }
        var remote = context.Connection.RemoteIpAddress;
        bool loopback = remote is null || IPAddress.IsLoopback(remote);
        if (!string.IsNullOrEmpty(apiToken))
        {
            var provided = context.Request.Headers["X-Api-Token"].FirstOrDefault()
                ?? context.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.Ordinal);
            if (provided is null || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(apiToken)))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "invalid or missing API token" });
                return;
            }
        }
        else if (!loopback)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Control Room API is local-only; set INTENTMESH_WEB_TOKEN to allow remote access." });
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness/readiness for orchestrators/proxies (no auth — they carry no run data).
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", () => runtime is not null && Directory.Exists(runsDir)
    ? Results.Json(new { status = "ready", keyId = keyProvider.KeyId })
    : Results.StatusCode(503));

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
    catch (Exception ex)
    {
        // Don't silently claim success: surface the persistence failure so the caller knows the run was
        // NOT durably recorded (the pipeline result is still returned).
        Console.Error.WriteLine($"WARN: could not persist run: {ex.Message}");
        http.Headers["X-Persist-Error"] = ex.Message.Replace('\n', ' ').Replace('\r', ' ');
    }

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
/// Serves the file AS STORED ON DISK (not re-derived), so a tampered artifact is shown verbatim and the
/// viewer agrees with /verify rather than masking tampering with a clean regenerated copy.
app.MapGet("/api/runs/{id}/artifact/{name}", (string id, string name) =>
{
    try
    {
        var json = store.ReadArtifact(id, name);
        return json is not null
            ? Results.Text(json, "application/json")
            : Results.NotFound(new { error = $"no artifact '{name}'", available = TraceBundleBuilder.ArtifactNames });
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
        // Sign through the SAME configured (rotation-aware) provider as /api/run — never the static
        // default — so an exported audit/bundle is signed with, and verifiable under, the host's key.
        "signed" => Results.Json(AuditSigner.Sign(result, keyProvider)),   // tamper-evident audit envelope
        "bundle" => Results.Json(TraceBundleBuilder.From(result, approvals.ToList(), keyProvider)),  // all 5 signed artifacts
        _ => Results.Text(AuditExporter.ToJson(result), "application/json"),
    };
});

app.Run();

// Approvals: node ids the user approved. The runtime only ever applies an approval to a
// full-authority Confirm node — a blocked zero-trust node stays blocked regardless.
record RunRequest(string? Prompt, string[]? Approvals);
record ExportRequest(string? Prompt, string[]? Approvals, string? Format);

// Exposed so the test project can host the app via WebApplicationFactory<Program>.
public partial class Program { }
