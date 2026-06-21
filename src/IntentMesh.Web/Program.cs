using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using IntentMesh.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

// IntentMesh Control Room — ASP.NET minimal API over IntentMesh.Core. Serves a dependency-free
// SPA (wwwroot) and runs the pipeline on demand. No CDN, no npm — robust offline.

var builder = WebApplication.CreateBuilder(args);

// Whether an upstream reverse proxy is trusted to assert identity / client ip, and the shared secret it
// must present. Read here (before the rate limiter) so the limiter can decide when X-Forwarded-For is
// trustworthy. (Re-read by the auth middleware below for the same trust decision.)
var trustedProxy = Environment.GetEnvironmentVariable("INTENTMESH_TRUSTED_PROXY") == "1";
var proxySecret = Environment.GetEnvironmentVariable("INTENTMESH_PROXY_SECRET");

// Rate limiting (built into the shared framework — no extra package). Partitions by client: behind the
// trusted proxy the forwarded X-Forwarded-For client ip, otherwise the socket ip. X-Forwarded-For is
// trusted ONLY behind the proxy, so a direct client can't rotate that header to evade the limit. A
// request with no resolvable client key (e.g. an in-process test server) is not limited. A global
// per-client cap covers run/replay/export/storage amplification; the stricter "auth" policy throttles
// credential brute force on POST /api/auth/token.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ClientKey(ctx, trustedProxy, proxySecret);
        return key is null
            ? RateLimitPartition.GetNoLimiter("unkeyed")
            : RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
    options.AddPolicy("auth", ctx =>
    {
        var key = ClientKey(ctx, trustedProxy, proxySecret);
        return key is null
            ? RateLimitPartition.GetNoLimiter("unkeyed")
            : RateLimitPartition.GetFixedWindowLimiter("auth:" + key, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});

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
var webToken = Environment.GetEnvironmentVariable("INTENTMESH_WEB_TOKEN");   // legacy single-token (default tenant)
var tokenMode = principals.Count > 0;
// A real (production-grade) auth boundary is token mode or trusted-proxy mode. The legacy shared
// WEB_TOKEN is a dev/local convenience and does NOT count as a production boundary.
var realAuthConfigured = tokenMode || trustedProxy;
var authConfigured = realAuthConfigured || webToken is not null;

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

// Production safety #2: refuse to start without a REAL authentication boundary. The legacy shared
// WEB_TOKEN does NOT qualify in Production — it's a single shared bearer with no per-principal identity.
if (app.Environment.IsProduction() && !realAuthConfigured
    && Environment.GetEnvironmentVariable("INTENTMESH_ALLOW_INSECURE_AUTH") != "1")
{
    Console.Error.WriteLine(
        "FATAL: refusing to start in Production without a real auth boundary. Provide INTENTMESH_PRINCIPALS + " +
        "INTENTMESH_AUTH_KEY (token mode) or INTENTMESH_TRUSTED_PROXY=1 + INTENTMESH_PROXY_SECRET (proxy mode). " +
        "The legacy INTENTMESH_WEB_TOKEN is dev-only. Set INTENTMESH_ALLOW_INSECURE_AUTH=1 for a deliberately local-only host.");
    return;
}

// Production safety #2b: trusted-proxy mode in Production MUST require a shared proxy secret — otherwise
// asserted X-Auth-* headers would be trusted from any loopback-presenting source.
if (app.Environment.IsProduction() && trustedProxy && string.IsNullOrEmpty(proxySecret)
    && Environment.GetEnvironmentVariable("INTENTMESH_ALLOW_INSECURE_AUTH") != "1")
{
    Console.Error.WriteLine(
        "FATAL: trusted-proxy mode in Production requires INTENTMESH_PROXY_SECRET — the upstream proxy must present " +
        "it as X-Proxy-Secret so injected X-Auth-* headers are trusted only from the real proxy hop.");
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

// Security headers on every response — a strict CSP (the SPA is dependency-free: its only script is the
// same-origin app.js), plus anti-sniff / anti-framing / referrer hygiene. style-src allows inline styles
// the dependency-free UI uses; no remote origins are permitted.
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    await next();
});

app.UseRateLimiter();

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
        context.Response.StatusCode = 413;   // Payload Too Large — fast reject when Content-Length is declared
        await context.Response.WriteAsJsonAsync(new { error = $"request body exceeds {MaxApiBodyBytes} bytes" });
        return;
    }
    // A declared Content-Length can be omitted or wrong (e.g. chunked transfer-encoding), so also cap the
    // actual bytes read: Kestrel enforces this limit while binding the body and rejects an over-limit
    // request regardless of the header. (Null/read-only on some test servers — handled gracefully.)
    var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeLimit is { IsReadOnly: false })
        sizeLimit.MaxRequestBodySize = MaxApiBodyBytes;

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
        // Legacy single shared token (dev/local only — see docs) → a default-tenant principal with
        // read+run, but NOT approver: a shared bearer must not be able to self-approve gated nodes, so
        // approvals still require a real approver principal (token mode) or trusted-proxy identity.
        if (ConstTimeEq(BearerOf(context), webToken))
            principal = new AuthPrincipal("operator", "default",
                Roles.Set(new[] { Roles.Operator, Roles.Viewer }));
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
// Readiness actually PROBES persistence: it writes and removes a temp file in the runs dir (the same
// kind of write + atomic move real persistence needs), so /readyz fails fast when the volume is
// read-only/full/unmounted — not merely when the directory is absent.
app.MapGet("/readyz", () =>
{
    if (runtime is null) return Results.StatusCode(503);
    try
    {
        var probe = Path.Combine(runsDir, ".readyz-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "ok");
        File.Move(probe, probe + ".moved", overwrite: true);   // exercises the atomic-move path persistence uses
        File.Delete(probe + ".moved");
    }
    catch { return Results.StatusCode(503); }
    return Results.Json(new { status = "ready", keyId = keyProvider.KeyId });
});

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
}).RequireRateLimiting("auth");

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

    // Fail CLOSED on persistence: a run that cannot be durably, verifiably recorded is treated as
    // failed (503) rather than returning 200 with an unsaved result — an audited action must not look
    // successful when its signed record was lost. The exception detail is logged server-side only.
    var store = StoreFor(p.TenantId);
    string runId;
    try
    {
        runId = store.Save(TraceBundleBuilder.From(result, new List<string>(), keyProvider));
        store.RecordOwner(runId, new RunOwner(p.PrincipalId, p.TenantId, NowUnix()));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: could not persist run: {ex}");
        return Results.Json(new { error = "run could not be durably persisted" }, statusCode: 503);
    }

    http.Response.Headers["X-Run-Id"] = runId;
    if (req.Approvals is { Length: > 0 })
        http.Response.Headers["X-Approvals-Ignored"] = "approve gated nodes via POST /api/runs/{id}/approve with a server-issued challenge";
    return Results.Json(result);
});

// ── Operator workflow: run history, detail, approval queue, replay diff, artifact viewer ──
// All reads are tenant-scoped: any authenticated member of the tenant may view its runs; a run id from
// another tenant simply isn't found (404), so existence never leaks across tenants.

app.MapGet("/api/runs", (HttpContext http) =>
{
    var p = Principal(http);
    if (!CanRead(p)) return Forbidden(Roles.Viewer);
    return Results.Json(StoreFor(p.TenantId).ListSummaries());
});

app.MapGet("/api/runs/{id}", (string id, HttpContext http) =>
{
    var p = Principal(http);
    if (!CanRead(p)) return Forbidden(Roles.Viewer);
    try { return Results.Json(StoreFor(p.TenantId).Load(id)); }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }
});

app.MapGet("/api/runs/{id}/artifact/{name}", (string id, string name, HttpContext http) =>
{
    var p = Principal(http);
    if (!CanRead(p)) return Forbidden(Roles.Viewer);
    try
    {
        var json = StoreFor(p.TenantId).ReadArtifact(id, name);
        return json is not null
            ? Results.Text(json, "application/json")
            : Results.NotFound(new { error = $"no artifact '{name}'", available = TraceBundleBuilder.ArtifactNames });
    }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }
});

app.MapGet("/api/runs/{id}/verify", (string id, HttpContext http) =>
{
    var p = Principal(http);
    if (!CanRead(p)) return Forbidden(Roles.Viewer);
    try
    {
        var store = StoreFor(p.TenantId);
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
    var p = Principal(http);
    if (!CanRead(p)) return Forbidden(Roles.Viewer);
    try
    {
        var saved = StoreFor(p.TenantId).Load(id);
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
/// Mint a signed approval challenge for each gated (Confirm) approval UNIT of a run. The unit is the
/// bare node id, except for a per-file destructive delete where it is "{nodeId}#{fileRef}" — so each
/// file gets its own challenge and granular consent is preserved. The challenge is bound to
/// {runId, unit, tenant}; it is the ONLY thing /approve will accept. Requires the approver role.
app.MapPost("/api/runs/{id}/challenges", (string id, HttpContext http) =>
{
    var p = Principal(http);
    if (!p.Has(Roles.Approver)) return Forbidden(Roles.Approver);
    TraceBundle saved;
    try { saved = StoreFor(p.TenantId).Load(id); }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }

    // Trust the stored run BEFORE re-running it: a tampered bundle must not become input to a freshly
    // signed approved run. (Mirrors RunReplay, which verifies before re-execution.)
    if (!TraceBundleBuilder.VerifySignature(saved, keyProvider))
        return Results.Json(new { error = "stored run failed integrity verification" }, statusCode: 409);

    var res = runtime.Run(saved.Prompt, Workspace.CreateDemo(), new HashSet<string>());
    var now = NowUnix();
    var exp = now + ChallengeTtlSeconds;
    var challenges = res.Policy.Where(x => x.RequiresConfirmation).SelectMany(x =>
        // No refs → one challenge for the bare node; per-file delete → one challenge per "node#ref".
        (x.ApprovalRefs.Count == 0 ? new[] { (unit: x.NodeId, fileRef: (string?)null) }
            : x.ApprovalRefs.Select(r => (unit: $"{x.NodeId}#{r}", fileRef: (string?)r)).ToArray())
        .Select(u => new
        {
            nodeId = x.NodeId,
            fileRef = u.fileRef,
            label = x.Label,
            reason = x.Reason,
            challenge = challengeSvc.Mint(id, u.unit, p.TenantId, now, exp, Nonce()),
            expiresAt = exp,
        })).ToList();
    return Results.Json(new { runId = id, challenges });
});

/// Apply approvals to a run. Only node ids attested by a valid server-issued challenge (matching this
/// run + tenant, unexpired) are approved; the prompt is re-run with that server-attested set and the
/// approved execution is persisted as a new signed run. Requires the approver role.
app.MapPost("/api/runs/{id}/approve", (string id, ApproveRequest req, HttpContext http) =>
{
    var p = Principal(http);
    if (!p.Has(Roles.Approver)) return Forbidden(Roles.Approver);
    var store = StoreFor(p.TenantId);
    TraceBundle saved;
    try { saved = store.Load(id); }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return Results.NotFound(new { error = $"no run '{id}'" }); }

    // Trust the stored run BEFORE re-running it under approval — a tampered bundle must never become
    // input to a newly signed approved run.
    if (!TraceBundleBuilder.VerifySignature(saved, keyProvider))
        return Results.Json(new { error = "stored run failed integrity verification" }, statusCode: 409);

    var now = NowUnix();
    var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var token in req.Challenges ?? Array.Empty<string>())
        if (challengeSvc.TryVerify(token, id, p.TenantId, now, out var unit))
            approved.Add(unit);   // unit is a bare node id, or "node#fileRef" for a per-file delete
    if (approved.Count == 0)
        return Results.Json(new { error = "no valid approval challenge presented" }, statusCode: 400);

    var result = runtime.Run(saved.Prompt, Workspace.CreateDemo(), approved);
    string newId;
    try
    {
        newId = store.Save(TraceBundleBuilder.From(result, approved.ToList(), keyProvider));
        store.RecordOwner(newId, new RunOwner(p.PrincipalId, p.TenantId, now));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: could not persist approved run: {ex}");
        return Results.Json(new { error = "approved run could not be durably persisted" }, statusCode: 503);
    }
    http.Response.Headers["X-Run-Id"] = newId;
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
// Caller-asserted approvals are NOT honored here: export would otherwise SIGN a bundle showing approved
// actions without going through server-issued challenges + approver authorization. Export always runs
// unapproved; to obtain a signed approved bundle, approve via /api/runs/{id}/approve (which persists it)
// and fetch it from /api/runs/{id}.
app.MapPost("/api/export", (ExportRequest req, HttpContext http) =>
{
    if (!Principal(http).Has(Roles.Operator)) return Forbidden(Roles.Operator);
    var prompt = (req.Prompt ?? "").Trim();
    if (string.IsNullOrEmpty(prompt)) return Results.BadRequest(new { error = "empty prompt" });
    var result = runtime.Run(prompt, Workspace.CreateDemo(), new HashSet<string>());
    return (req.Format ?? "json").ToLowerInvariant() switch
    {
        "md" or "markdown" => Results.Text(AuditExporter.ToMarkdown(result), "text/markdown"),
        "signed" => Results.Json(AuditSigner.Sign(result, keyProvider)),
        "bundle" => Results.Json(TraceBundleBuilder.From(result, new List<string>(), keyProvider)),
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

// Rate-limit partition key. X-Forwarded-For is honored ONLY when the request comes through the trusted
// proxy (proxy mode + matching X-Proxy-Secret, or loopback when no secret is configured) — so a direct
// client cannot rotate that header to dodge the limit. Otherwise the socket ip is used. Null when
// neither is resolvable (e.g. the in-process test server) — such requests are not rate-limited.
static string? ClientKey(HttpContext c, bool trustedProxy, string? proxySecret)
{
    var remote = c.Connection.RemoteIpAddress;
    var proxyTrusted = trustedProxy && (proxySecret is not null
        ? ConstTimeEq(c.Request.Headers["X-Proxy-Secret"].ToString(), proxySecret)
        : remote is null || IPAddress.IsLoopback(remote));
    if (proxyTrusted)
    {
        var fwd = c.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd)) return fwd.Split(',')[0].Trim();
    }
    return remote?.ToString();
}

static bool CanRead(AuthPrincipal p)
    => p.Has(Roles.Viewer) || p.Has(Roles.Operator) || p.Has(Roles.Approver);   // admin covered by Has

// Per-tenant run store: each tenant's runs live under {runsDir}/t/{tenant} — isolation by construction.
// Defense-in-depth: the tenant id is already validated when the principal is resolved, but re-validate
// here and confirm the resolved directory stays under the runs root before constructing the store, so a
// traversal-shaped tenant can never escape (no store is created for an out-of-bounds path).
FileRunArtifactStore StoreFor(string tenant)
{
    if (!AuthIds.IsValid(tenant))
        throw new ArgumentException($"invalid tenant id '{tenant}'", nameof(tenant));
    var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runsDir));
    var dir = Path.GetFullPath(Path.Combine(rootFull, "t", tenant));
    var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (!dir.StartsWith(rootFull + Path.DirectorySeparatorChar, cmp))
        throw new ArgumentException($"tenant '{tenant}' resolves outside the runs root", nameof(tenant));
    return new FileRunArtifactStore(dir);
}

// Approvals: gated node ids are only ever applied when attested by a server-issued challenge.
record RunRequest(string? Prompt, string[]? Approvals);
record ExportRequest(string? Prompt, string[]? Approvals, string? Format);
record TokenRequest(string? ApiKey);
record ApproveRequest(string[]? Challenges);

// Exposed so the test project can host the app via WebApplicationFactory<Program>.
public partial class Program { }
