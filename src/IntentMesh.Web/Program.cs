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

// Persistent run store ROOT. Runs are partitioned per tenant under {root}/t/{tenantId}/ so one tenant
// can never read another's runs (isolation by construction). INTENTMESH_RUNS_DIR overrides ./runs.
var runsDir = Environment.GetEnvironmentVariable("INTENTMESH_RUNS_DIR")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "runs");
Directory.CreateDirectory(runsDir);

// The audit key provider, rotation-aware: prior keys (INTENTMESH_AUDIT_PRIOR_KEYS) let runs signed
// before a key rotation still verify/replay. Sign and verify go through THIS provider so a bundle's
// recorded key id always resolves to the key that produced it.
var keyProvider = AuditKeyProviders.FromEnvironment();

// ── Multi-tenant authz configuration ─────────────────────────────────────────────────────────────
// Two interchangeable identity modes (see docs/DEPLOYMENT.md):
//   • Built-in tokens  — a principal store (INTENTMESH_PRINCIPALS) exchanges an API key for an
//                        HMAC-signed session token at POST /api/auth/token; every /api call presents it.
//   • Trusted proxy    — INTENTMESH_TRUSTED_PROXY=1: an upstream IdP/proxy injects verified
//                        X-Auth-Principal / X-Auth-Tenant / X-Auth-Roles (optionally gated by a shared
//                        X-Proxy-Secret). The proxy owns login; we enforce tenant/role downstream.
// Approval challenges and session tokens are signed with INTENTMESH_AUTH_KEY (>=128-bit) when set,
// else they fall back to the audit key for local/dev hosts.
var authKeyRaw = Environment.GetEnvironmentVariable("INTENTMESH_AUTH_KEY");
var effectiveAuthKey = authKeyRaw is not null ? AuthKeys.Parse(authKeyRaw) : keyProvider.GetKey();
var tokenSvc = new AuthTokenService(effectiveAuthKey);
var challengeSvc = new ApprovalChallengeService(effectiveAuthKey);
var principals = PrincipalStore.FromEnvironment();
var trustedProxy = Environment.GetEnvironmentVariable("INTENTMESH_TRUSTED_PROXY") == "1";
var proxySecret = Environment.GetEnvironmentVariable("INTENTMESH_PROXY_SECRET");
var webToken = Environment.GetEnvironmentVariable("INTENTMESH_WEB_TOKEN");   // legacy single-token (default tenant)
var tokenMode = principals.Count > 0;
var authConfigured = tokenMode || trustedProxy || webToken is not null;

const long SessionTtlSeconds = 3600;     // 1h session tokens
const long ChallengeTtlSeconds = 600;    // 10m approval challenges
const long MaxApiBodyBytes = 256 * 1024; // prompts/approvals are tiny; reject oversized bodies (DoS guard)

// Production safety #1: refuse to start with only the INSECURE demo audit key — signed audits would be
// forgeable by anyone who knows the (public) demo key.
if (app.Environment.IsProduction()
    && keyProvider.KeyId == AuditSigner.DemoKeyId
    && Environment.GetEnvironmentVariable("INTENTMESH_ALLOW_INSECURE_KEY") != "1")
{
    Console.Error.WriteLine(
        "FATAL: refusing to start in Production with the INSECURE demo audit key — signed audits would be forgeable. " +
        "Set INTENTMESH_AUDIT_KEY (>=128-bit; base64 or utf8), or set INTENTMESH_ALLOW_INSECURE_KEY=1 for a deliberately local-only host.");
    return;
}

// Production safety #2: refuse to start without an authentication boundary — otherwise the /api surface
// would fall back to local-only/dev access on a public host.
if (app.Environment.IsProduction() && !authConfigured
    && Environment.GetEnvironmentVariable("INTENTMESH_ALLOW_INSECURE_AUTH") != "1")
{
    Console.Error.WriteLine(
        "FATAL: refusing to start in Production without authentication configured. Provide INTENTMESH_PRINCIPALS + " +
        "INTENTMESH_AUTH_KEY (token mode) or INTENTMESH_TRUSTED_PROXY=1 (proxy mode), or set " +
        "INTENTMESH_ALLOW_INSECURE_AUTH=1 for a deliberately local-only host.");
    return;
}

// Production safety #3: token mode must use a DEDICATED auth key, not the audit-key fallback (purpose
// separation between audit signing and session/approval signing).
if (app.Environment.IsProduction() && tokenMode && authKeyRaw is null
    && Environment.GetEnvironmentVariable("INTENTMESH_ALLOW_INSECURE_AUTH") != "1")
{
    Console.Error.WriteLine(
        "FATAL: token auth in Production requires a dedicated INTENTMESH_AUTH_KEY (>=128-bit), separate from the audit key.");
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

// ── Auth middleware: resolve the calling principal for every /api request ──────────────────────────
// Precedence: trusted-proxy > built-in token > legacy single-token > dev-loopback. The resolved
// principal is attached to HttpContext.Items["principal"]; endpoints role-gate off it and scope every
// run-store access to the principal's tenant. /api/auth/token is the only unauthenticated /api route.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api")) { await next(); return; }

    if (context.Request.ContentLength > MaxApiBodyBytes)
    {
        context.Response.StatusCode = 413;   // Payload Too Large
        await context.Response.WriteAsJsonAsync(new { error = $"request body exceeds {MaxApiBodyBytes} bytes" });
        return;
    }

    if (path.StartsWithSegments("/api/auth/token")) { await next(); return; }   // login endpoint is open

    var remote = context.Connection.RemoteIpAddress;
    var loopback = remote is null || IPAddress.IsLoopback(remote);
    AuthPrincipal? principal = null;

    if (trustedProxy)
    {
        // Trust X-Auth-* only from the proxy hop: require the shared secret when configured, else loopback.
        var proxyTrusted = proxySecret is not null
            ? ConstTimeEq(context.Request.Headers["X-Proxy-Secret"].ToString(), proxySecret)
            : loopback;
        if (proxyTrusted && TrustedProxyAuth.TryFromHeaders(
                context.Request.Headers["X-Auth-Principal"].ToString(),
                context.Request.Headers["X-Auth-Tenant"].ToString(),
                context.Request.Headers["X-Auth-Roles"].ToString(), out var pp))
            principal = pp;
    }
    else if (tokenMode)
    {
        if (tokenSvc.TryVerify(BearerOf(context), NowUnix(), out var pp)) principal = pp;
    }
    else if (webToken is not null)
    {
        // Legacy single shared token → a default-tenant principal with read+run+approve.
        if (ConstTimeEq(BearerOf(context), webToken))
            principal = new AuthPrincipal("operator", "default",
                Roles.Set(new[] { Roles.Operator, Roles.Approver, Roles.Viewer }));
    }
    else if (loopback)
    {
        // Dev mode (no auth configured): loopback gets a full-access dev principal. Refused for Production
        // by the startup guard above; refused for non-loopback below.
        principal = new AuthPrincipal("dev", "dev",
            Roles.Set(new[] { Roles.Operator, Roles.Approver, Roles.Viewer, Roles.Admin }));
    }

    if (principal is null)
    {
        if (!authConfigured && !loopback)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Control Room API is local-only; configure authentication (INTENTMESH_PRINCIPALS / INTENTMESH_TRUSTED_PROXY) to allow remote access." });
        }
        else
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "authentication required" });
        }
        return;
    }

    context.Items["principal"] = principal;
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness/readiness for orchestrators/proxies (no auth — they carry no run data).
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", () => runtime is not null && Directory.Exists(runsDir)
    ? Results.Json(new { status = "ready", keyId = keyProvider.KeyId })
    : Results.StatusCode(503));

// ── Authentication endpoints ───────────────────────────────────────────────────────────────────
/// Exchange an API key for a short-lived signed session token (built-in token mode only).
app.MapPost("/api/auth/token", (TokenRequest req) =>
{
    if (!tokenMode)
        return Results.Json(new { error = "built-in token auth is not enabled on this host" }, statusCode: 404);
    if (!principals.TryAuthenticate(req.ApiKey, out var p))
        return Results.Json(new { error = "invalid API key" }, statusCode: 401);
    var now = NowUnix();
    var exp = now + SessionTtlSeconds;
    return Results.Json(new
    {
        token = tokenSvc.Mint(p, now, exp, Nonce()),
        principal = p.PrincipalId,
        tenant = p.TenantId,
        roles = p.Roles,
        expiresAt = exp,
    });
});

/// The authenticated caller's identity (principal, tenant, roles).
app.MapGet("/api/auth/whoami", (HttpContext http) =>
{
    var p = Principal(http);
    return Results.Json(new { principal = p.PrincipalId, tenant = p.TenantId, roles = p.Roles });
});

app.MapGet("/api/demos", () => Results.Json(demos));

app.MapPost("/api/run", (RunRequest req, HttpContext http) =>
{
    var p = Principal(http);
    if (!p.Has(Roles.Operator)) return Forbidden(Roles.Operator);
    var prompt = (req.Prompt ?? "").Trim();
    if (string.IsNullOrEmpty(prompt)) return Results.BadRequest(new { error = "empty prompt" });

    // Caller-asserted approvals are NOT honored: a gated node is only ever approved via a server-issued
    // challenge (POST /api/runs/{id}/approve). The first pass always runs with no approvals.
    var result = runtime.Run(prompt, Workspace.CreateDemo(), new HashSet<string>());

    var store = StoreFor(p.TenantId);
    try
    {
        var runId = store.Save(TraceBundleBuilder.From(result, new List<string>(), keyProvider));
        store.RecordOwner(runId, new RunOwner(p.PrincipalId, p.TenantId, NowUnix()));
        http.Response.Headers["X-Run-Id"] = runId;
        if (req.Approvals is { Length: > 0 })
            http.Response.Headers["X-Approvals-Ignored"] = "approve gated nodes via POST /api/runs/{id}/approve with a server-issued challenge";
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"WARN: could not persist run: {ex.Message}");
        http.Response.Headers["X-Persist-Error"] = ex.Message.Replace('\n', ' ').Replace('\r', ' ');
    }

    return Results.Json(result);
});

// ── Operator workflow: run history, detail, approval queue, replay diff, artifact viewer ──
// All reads are tenant-scoped: any authenticated member of the tenant may view its runs; a run id from
// another tenant simply isn't found (404), so existence never leaks across tenants.

app.MapGet("/api/runs", (HttpContext http) => Results.Json(StoreFor(Principal(http).TenantId).ListSummaries()));

app.MapGet("/api/runs/{id}", (string id, HttpContext http) =>
{
    try { return Results.Json(StoreFor(Principal(http).TenantId).Load(id)); }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }
});

app.MapGet("/api/runs/{id}/artifact/{name}", (string id, string name, HttpContext http) =>
{
    try
    {
        var json = StoreFor(Principal(http).TenantId).ReadArtifact(id, name);
        return json is not null
            ? Results.Text(json, "application/json")
            : Results.NotFound(new { error = $"no artifact '{name}'", available = TraceBundleBuilder.ArtifactNames });
    }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }
});

app.MapGet("/api/runs/{id}/verify", (string id, HttpContext http) =>
{
    try
    {
        var store = StoreFor(Principal(http).TenantId);
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

app.MapPost("/api/runs/{id}/replay", (string id, HttpContext http) =>
{
    try
    {
        var saved = StoreFor(Principal(http).TenantId).Load(id);
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

// ── Server-issued approval flow (replaces caller-asserted approvals) ───────────────────────────────
/// Mint a signed approval challenge for each gated (Confirm) node of a run. The challenge is bound to
/// {runId, nodeId, tenant} — it is the ONLY thing /approve will accept. Requires the approver role.
app.MapPost("/api/runs/{id}/challenges", (string id, HttpContext http) =>
{
    var p = Principal(http);
    if (!p.Has(Roles.Approver)) return Forbidden(Roles.Approver);
    TraceBundle saved;
    try { saved = StoreFor(p.TenantId).Load(id); }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }

    var res = runtime.Run(saved.Prompt, Workspace.CreateDemo(), new HashSet<string>());
    var now = NowUnix();
    var exp = now + ChallengeTtlSeconds;
    var challenges = res.Policy.Where(x => x.RequiresConfirmation).Select(x => new
    {
        nodeId = x.NodeId,
        label = x.Label,
        reason = x.Reason,
        challenge = challengeSvc.Mint(id, x.NodeId, p.TenantId, now, exp, Nonce()),
        expiresAt = exp,
    }).ToList();
    return Results.Json(new { runId = id, challenges });
});

/// Apply approvals to a run. Only node ids attested by a valid server-issued challenge (matching this
/// run + tenant, unexpired) are approved; the prompt is re-run with that server-attested set and the
/// approved execution is persisted as a new signed run. Requires the approver role.
app.MapPost("/api/runs/{id}/approve", (string id, ApproveRequest req, HttpContext http) =>
{
    var p = Principal(http);
    if (!p.Has(Roles.Approver)) return Forbidden(Roles.Approver);
    TraceBundle saved;
    try { saved = StoreFor(p.TenantId).Load(id); }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }

    var now = NowUnix();
    var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var token in req.Challenges ?? Array.Empty<string>())
        if (challengeSvc.TryVerify(token, id, p.TenantId, now, out var node))
            approved.Add(node);
    if (approved.Count == 0)
        return Results.Json(new { error = "no valid approval challenge presented" }, statusCode: 400);

    var result = runtime.Run(saved.Prompt, Workspace.CreateDemo(), approved);
    var store = StoreFor(p.TenantId);
    try
    {
        var newId = store.Save(TraceBundleBuilder.From(result, approved.ToList(), keyProvider));
        store.RecordOwner(newId, new RunOwner(p.PrincipalId, p.TenantId, now));
        http.Response.Headers["X-Run-Id"] = newId;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"WARN: could not persist approved run: {ex.Message}");
        http.Response.Headers["X-Persist-Error"] = ex.Message.Replace('\n', ' ').Replace('\r', ' ');
    }
    return Results.Json(result);
});

/// "Why blocked / what would approval do" — the approval-queue reasoning view (requires operator).
app.MapPost("/api/explain", (RunRequest req, HttpContext http) =>
{
    if (!Principal(http).Has(Roles.Operator)) return Forbidden(Roles.Operator);
    var prompt = (req.Prompt ?? "").Trim();
    if (string.IsNullOrEmpty(prompt)) return Results.BadRequest(new { error = "empty prompt" });
    var approvals = (req.Approvals ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    return Results.Json(RunExplain.Explain(runtime, prompt, Workspace.CreateDemo, approvals));
});

// Export the run as the canonical, deterministic audit artifact (replayable; no timestamps).
app.MapPost("/api/export", (ExportRequest req, HttpContext http) =>
{
    if (!Principal(http).Has(Roles.Operator)) return Forbidden(Roles.Operator);
    var prompt = (req.Prompt ?? "").Trim();
    if (string.IsNullOrEmpty(prompt)) return Results.BadRequest(new { error = "empty prompt" });
    var approvals = (req.Approvals ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var result = runtime.Run(prompt, Workspace.CreateDemo(), approvals);
    return (req.Format ?? "json").ToLowerInvariant() switch
    {
        "md" or "markdown" => Results.Text(AuditExporter.ToMarkdown(result), "text/markdown"),
        "signed" => Results.Json(AuditSigner.Sign(result, keyProvider)),
        "bundle" => Results.Json(TraceBundleBuilder.From(result, approvals.ToList(), keyProvider)),
        _ => Results.Text(AuditExporter.ToJson(result), "application/json"),
    };
});

app.Run();

// ── Helpers (top-level local functions) ────────────────────────────────────────────────────────
static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
static string Nonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
static AuthPrincipal Principal(HttpContext c) => (AuthPrincipal)c.Items["principal"]!;
static IResult Forbidden(string role) => Results.Json(new { error = $"requires the '{role}' role" }, statusCode: 403);

static string? BearerOf(HttpContext c)
    => c.Request.Headers["X-Api-Token"].FirstOrDefault()
       ?? c.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.Ordinal);

static bool ConstTimeEq(string? a, string b)
    => !string.IsNullOrEmpty(a)
       && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

// Per-tenant run store: each tenant's runs live under {runsDir}/t/{tenant} — isolation by construction.
FileRunArtifactStore StoreFor(string tenant) => new(Path.Combine(runsDir, "t", tenant));

// Approvals: gated node ids are only ever applied when attested by a server-issued challenge.
record RunRequest(string? Prompt, string[]? Approvals);
record ExportRequest(string? Prompt, string[]? Approvals, string? Format);
record TokenRequest(string? ApiKey);
record ApproveRequest(string[]? Challenges);

// Exposed so the test project can host the app via WebApplicationFactory<Program>.
public partial class Program { }
