using System.Net;
using System.Net.Http.Json;
using IntentMesh.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// End-to-end multi-tenant authz over the Control Room via WebApplicationFactory: built-in token mode
/// (API key → signed session token), tenant isolation (a run from another tenant is 404), role gating
/// (a viewer cannot run the pipeline), the server-issued approval flow (caller-asserted approvals are
/// ignored; only a server-minted challenge approves a gated node), and trusted-proxy header mode.
/// </summary>
[Collection("web")]   // serialize: these tests mutate process env vars the web host reads at startup
public sealed class WebAuthzTests
{
    private const string AuthKey = "intentmesh-web-auth-key-0123456789ab";   // >=16 bytes
    private const string Demo1 = "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";

    private const string AliceKey = "sk-alice-secret";   // acme: operator+approver+viewer
    private const string CarolKey = "sk-carol-secret";   // acme: viewer only
    private const string BobKey = "sk-bob-secret";       // globex: operator+approver+viewer
    private const string DanKey = "sk-dan-secret";       // acme: NO roles

    private static string PrincipalsJson() =>
        "[" +
        $"{{\"id\":\"alice\",\"tenant\":\"acme\",\"roles\":[\"operator\",\"approver\",\"viewer\"],\"apiKeyHash\":\"{PrincipalStore.HashApiKey(AliceKey)}\"}}," +
        $"{{\"id\":\"carol\",\"tenant\":\"acme\",\"roles\":[\"viewer\"],\"apiKeyHash\":\"{PrincipalStore.HashApiKey(CarolKey)}\"}}," +
        $"{{\"id\":\"dan\",\"tenant\":\"acme\",\"roles\":[],\"apiKeyHash\":\"{PrincipalStore.HashApiKey(DanKey)}\"}}," +
        $"{{\"id\":\"bob\",\"tenant\":\"globex\",\"roles\":[\"operator\",\"approver\",\"viewer\"],\"apiKeyHash\":\"{PrincipalStore.HashApiKey(BobKey)}\"}}" +
        "]";

    private static (WebApplicationFactory<Program> factory, string runsDir) MakeTokenMode()
    {
        var runsDir = Path.Combine(Path.GetTempPath(), "im-authz-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("INTENTMESH_RUNS_DIR", runsDir);
        Environment.SetEnvironmentVariable("INTENTMESH_AUTH_KEY", AuthKey);
        Environment.SetEnvironmentVariable("INTENTMESH_PRINCIPALS", PrincipalsJson());
        Environment.SetEnvironmentVariable("INTENTMESH_WEB_TOKEN", null);
        Environment.SetEnvironmentVariable("INTENTMESH_TRUSTED_PROXY", null);
        Environment.SetEnvironmentVariable("INTENTMESH_PROXY_SECRET", null);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        return (factory, runsDir);
    }

    private static (WebApplicationFactory<Program> factory, string runsDir) MakeProxyMode(string proxySecret)
    {
        var runsDir = Path.Combine(Path.GetTempPath(), "im-proxy-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("INTENTMESH_RUNS_DIR", runsDir);
        Environment.SetEnvironmentVariable("INTENTMESH_TRUSTED_PROXY", "1");
        Environment.SetEnvironmentVariable("INTENTMESH_PROXY_SECRET", proxySecret);
        Environment.SetEnvironmentVariable("INTENTMESH_PRINCIPALS", null);
        Environment.SetEnvironmentVariable("INTENTMESH_WEB_TOKEN", null);
        Environment.SetEnvironmentVariable("INTENTMESH_AUTH_KEY", AuthKey);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        return (factory, runsDir);
    }

    private static void Cleanup(WebApplicationFactory<Program> f, string runsDir)
    {
        f.Dispose();
        foreach (var v in new[] { "INTENTMESH_RUNS_DIR", "INTENTMESH_AUTH_KEY", "INTENTMESH_PRINCIPALS",
                                  "INTENTMESH_WEB_TOKEN", "INTENTMESH_TRUSTED_PROXY", "INTENTMESH_PROXY_SECRET" })
            Environment.SetEnvironmentVariable(v, null);
        try { Directory.Delete(runsDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed record TokenResp(string token, string principal, string tenant, string[] roles, long expiresAt);
    private sealed record WhoResp(string principal, string tenant, string[] roles);
    private sealed record Challenge(string nodeId, string? fileRef, string label, string reason, string challenge, long expiresAt);
    private sealed record ChallengesResp(string runId, Challenge[] challenges);
    private sealed record BundleApprovals(string bundleSignature, string[] approvals);

    private static async Task<string> TokenFor(WebApplicationFactory<Program> f, string apiKey)
    {
        var resp = await f.CreateClient().PostAsJsonAsync("/api/auth/token", new { apiKey });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResp>())!.token;
    }

    private static async Task<HttpClient> ClientFor(WebApplicationFactory<Program> f, string apiKey)
    {
        var token = await TokenFor(f, apiKey);
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Token", token);
        return c;
    }

    [Fact]
    public async Task Unauthenticated_api_is_rejected_in_token_mode()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/demos")).StatusCode);
            // A bad API key cannot get a token.
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await f.CreateClient().PostAsJsonAsync("/api/auth/token", new { apiKey = "nope" })).StatusCode);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task Api_key_exchanges_for_a_token_and_whoami_returns_the_principal()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var c = await ClientFor(f, AliceKey);
            var who = await c.GetFromJsonAsync<WhoResp>("/api/auth/whoami");
            Assert.Equal("alice", who!.principal);
            Assert.Equal("acme", who.tenant);
            Assert.Contains("approver", who.roles);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task A_run_is_isolated_to_its_tenant()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var alice = await ClientFor(f, AliceKey);
            var run = await alice.PostAsJsonAsync("/api/run", new { prompt = Demo1 });
            run.EnsureSuccessStatusCode();
            var id = run.Headers.GetValues("X-Run-Id").First();

            // Owner (acme) can read it.
            Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/runs/{id}")).StatusCode);

            // A principal in another tenant cannot — existence does not leak across tenants.
            var bob = await ClientFor(f, BobKey);
            Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/runs/{id}")).StatusCode);
            var bobHistory = await bob.GetStringAsync("/api/runs");
            Assert.DoesNotContain(id, bobHistory);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task A_viewer_cannot_run_the_pipeline_but_an_operator_can()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var carol = await ClientFor(f, CarolKey);   // viewer only
            Assert.Equal(HttpStatusCode.Forbidden,
                (await carol.PostAsJsonAsync("/api/run", new { prompt = Demo1 })).StatusCode);
            // ...but a viewer CAN read its tenant's run history.
            Assert.Equal(HttpStatusCode.OK, (await carol.GetAsync("/api/runs")).StatusCode);

            var alice = await ClientFor(f, AliceKey);   // operator
            Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/run", new { prompt = Demo1 })).StatusCode);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task Caller_asserted_approvals_are_ignored_and_only_a_server_issued_challenge_approves()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var alice = await ClientFor(f, AliceKey);   // operator + approver

            // Caller-asserted approvals on /api/run are NOT honored.
            var run = await alice.PostAsJsonAsync("/api/run", new { prompt = Demo1, approvals = new[] { "n1", "n2" } });
            run.EnsureSuccessStatusCode();
            Assert.True(run.Headers.Contains("X-Approvals-Ignored"));
            var id = run.Headers.GetValues("X-Run-Id").First();

            // The server issues challenges for the gated nodes.
            var chResp = await alice.PostAsync($"/api/runs/{id}/challenges", null);
            chResp.EnsureSuccessStatusCode();
            var challenges = (await chResp.Content.ReadFromJsonAsync<ChallengesResp>())!;
            Assert.NotEmpty(challenges.challenges);

            // A forged/garbage challenge is rejected — nothing gets approved.
            var forged = await alice.PostAsJsonAsync($"/api/runs/{id}/approve",
                new { challenges = new[] { "im1.ZGVhZA.ZGVhZA" } });
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

            // The genuine server-issued challenge approves: the approved run is a NEW content-addressed
            // run (its bundle now includes the approval), proving the approval actually took effect.
            var approve = await alice.PostAsJsonAsync($"/api/runs/{id}/approve",
                new { challenges = challenges.challenges.Select(x => x.challenge).ToArray() });
            approve.EnsureSuccessStatusCode();
            var newId = approve.Headers.GetValues("X-Run-Id").First();
            Assert.NotEqual(id, newId);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task The_approver_role_is_required_to_approve()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var alice = await ClientFor(f, AliceKey);
            var id = (await alice.PostAsJsonAsync("/api/run", new { prompt = Demo1 }))
                .Headers.GetValues("X-Run-Id").First();

            var carol = await ClientFor(f, CarolKey);   // viewer only — no approver role
            Assert.Equal(HttpStatusCode.Forbidden, (await carol.PostAsync($"/api/runs/{id}/challenges", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await carol.PostAsJsonAsync($"/api/runs/{id}/approve", new { challenges = Array.Empty<string>() })).StatusCode);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task A_principal_with_no_role_cannot_read_runs()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var dan = await ClientFor(f, DanKey);   // authenticated, but no roles
            Assert.Equal(HttpStatusCode.Forbidden, (await dan.GetAsync("/api/runs")).StatusCode);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task Export_does_not_honor_caller_supplied_approvals()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var alice = await ClientFor(f, AliceKey);
            var resp = await alice.PostAsJsonAsync("/api/export",
                new { prompt = Demo1, approvals = new[] { "n1", "n2" }, format = "bundle" });
            resp.EnsureSuccessStatusCode();
            var bundle = (await resp.Content.ReadFromJsonAsync<BundleApprovals>())!;
            Assert.Empty(bundle.approvals);   // caller approvals ignored — the signed bundle has none
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task A_tampered_stored_run_fails_integrity_before_approval()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var alice = await ClientFor(f, AliceKey);
            var id = (await alice.PostAsJsonAsync("/api/run", new { prompt = Demo1 }))
                .Headers.GetValues("X-Run-Id").First();

            // Tamper the stored signed bundle (acme tenant partition) so its signature no longer verifies.
            var bundlePath = Path.Combine(runs, "t", "acme", id, "bundle.json");
            File.WriteAllText(bundlePath, File.ReadAllText(bundlePath).Replace("Friday", "Monday"));

            // Both challenge minting and approval must refuse to re-run a bundle that fails verification.
            Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync($"/api/runs/{id}/challenges", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,
                (await alice.PostAsJsonAsync($"/api/runs/{id}/approve", new { challenges = new[] { "x" } })).StatusCode);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task Per_file_delete_confirmations_are_minted_and_approved_per_file()
    {
        const string deletePrompt = "Clean up my downloads and delete anything that looks like junk.";
        var (f, runs) = MakeTokenMode();
        try
        {
            var alice = await ClientFor(f, AliceKey);
            var id = (await alice.PostAsJsonAsync("/api/run", new { prompt = deletePrompt }))
                .Headers.GetValues("X-Run-Id").First();

            var chResp = await alice.PostAsync($"/api/runs/{id}/challenges", null);
            chResp.EnsureSuccessStatusCode();
            var challenges = (await chResp.Content.ReadFromJsonAsync<ChallengesResp>())!;

            // A destructive delete is offered as PER-FILE challenges (node#fileRef), not one node-wide grant.
            Assert.Contains(challenges.challenges, c => c.fileRef is not null);

            var approve = await alice.PostAsJsonAsync($"/api/runs/{id}/approve",
                new { challenges = challenges.challenges.Select(x => x.challenge).ToArray() });
            approve.EnsureSuccessStatusCode();
            Assert.NotEqual(id, approve.Headers.GetValues("X-Run-Id").First());
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task Auth_endpoint_is_rate_limited_per_client()
    {
        var (f, runs) = MakeTokenMode();
        try
        {
            var codes = new List<HttpStatusCode>();
            for (int i = 0; i < 13; i++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/token")
                { Content = JsonContent.Create(new { apiKey = "nope" }) };
                req.Headers.Add("X-Forwarded-For", "203.0.113.7");   // a fixed client ip → its own partition
                codes.Add((await f.CreateClient().SendAsync(req)).StatusCode);
            }
            // The "auth" policy permits 10/min per client; the surplus is rejected with 429.
            Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task Trusted_proxy_headers_are_honored_only_with_the_proxy_secret()
    {
        var (f, runs) = MakeProxyMode("proxy-shared-secret");
        try
        {
            // With the shared secret, the proxy-asserted identity is accepted.
            var trusted = f.CreateClient();
            trusted.DefaultRequestHeaders.Add("X-Proxy-Secret", "proxy-shared-secret");
            trusted.DefaultRequestHeaders.Add("X-Auth-Principal", "dave");
            trusted.DefaultRequestHeaders.Add("X-Auth-Tenant", "initech");
            trusted.DefaultRequestHeaders.Add("X-Auth-Roles", "operator,approver,viewer");
            var who = await trusted.GetFromJsonAsync<WhoResp>("/api/auth/whoami");
            Assert.Equal("dave", who!.principal);
            Assert.Equal("initech", who.tenant);

            // Without the secret, spoofed X-Auth-* headers are NOT trusted.
            var spoof = f.CreateClient();
            spoof.DefaultRequestHeaders.Add("X-Auth-Principal", "dave");
            spoof.DefaultRequestHeaders.Add("X-Auth-Tenant", "initech");
            spoof.DefaultRequestHeaders.Add("X-Auth-Roles", "admin");
            Assert.Equal(HttpStatusCode.Unauthorized, (await spoof.GetAsync("/api/auth/whoami")).StatusCode);
        }
        finally { Cleanup(f, runs); }
    }
}
