using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Test-gates the Control Room release surface via WebApplicationFactory: the API is reachable from
/// loopback, token auth is enforced when configured (and the right token works), and a run persists
/// into history. Runs in the Development environment so the demo-key production startup guard is not
/// triggered; an isolated temp runs dir keeps it side-effect-free.
/// </summary>
[Collection("web")]   // serialize: these tests mutate process env vars the web host reads at startup
public sealed class WebTests
{
    private static (WebApplicationFactory<Program> factory, string runsDir) Make()
    {
        var runsDir = Path.Combine(Path.GetTempPath(), "im-web-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("INTENTMESH_RUNS_DIR", runsDir);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        return (factory, runsDir);
    }

    private static void Cleanup(WebApplicationFactory<Program> f, string runsDir)
    {
        f.Dispose();
        Environment.SetEnvironmentVariable("INTENTMESH_RUNS_DIR", null);
        Environment.SetEnvironmentVariable("INTENTMESH_WEB_TOKEN", null);
        try { Directory.Delete(runsDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Loopback_api_is_reachable_without_a_token()
    {
        Environment.SetEnvironmentVariable("INTENTMESH_WEB_TOKEN", null);
        var (f, runs) = Make();
        try
        {
            var resp = await f.CreateClient().GetAsync("/api/demos");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task Token_is_enforced_when_configured()
    {
        Environment.SetEnvironmentVariable("INTENTMESH_WEB_TOKEN", "s3cret-token");
        var (f, runs) = Make();
        try
        {
            var client = f.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/demos")).StatusCode);

            client.DefaultRequestHeaders.Add("X-Api-Token", "s3cret-token");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/demos")).StatusCode);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task Health_and_readiness_endpoints_respond()
    {
        Environment.SetEnvironmentVariable("INTENTMESH_WEB_TOKEN", "tok");   // even token-gated, health is open
        var (f, runs) = Make();
        try
        {
            var c = f.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/readyz")).StatusCode);
        }
        finally { Cleanup(f, runs); }
    }

    [Fact]
    public async Task A_run_persists_and_appears_in_history()
    {
        Environment.SetEnvironmentVariable("INTENTMESH_WEB_TOKEN", null);
        var (f, runs) = Make();
        try
        {
            var client = f.CreateClient();
            var resp = await client.PostAsJsonAsync("/api/run", new { prompt = "Plan my Friday and draft Sarah the notes." });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(resp.Headers.Contains("X-Run-Id"), "a persisted run must return X-Run-Id");

            var history = await client.GetStringAsync("/api/runs");
            Assert.Contains("runId", history);
        }
        finally { Cleanup(f, runs); }
    }
}

/// <summary>Serializes the web tests (they set process-wide env vars the host reads at startup).</summary>
[CollectionDefinition("web", DisableParallelization = true)]
public sealed class WebTestCollection { }
