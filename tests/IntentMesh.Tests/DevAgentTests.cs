using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// v0.3 developer-agent demo: the same boundary applied to code. Proves shell is blocked by
/// default (allow-list only — even for the user), secrets can't enter a diff/PR, edits/PRs are
/// never pushed, and an injected shell command from repo content is blocked as zero-trust.
/// </summary>
public sealed class DevAgentTests
{
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();
    private const string DevPrompt = "Fix the failing test in the parser, run the tests, deploy to production, and open a pull request.";

    private static string Id(RunResult r, string kind, string contains) =>
        r.Nodes.First(n => n.Type == kind && n.Label.Contains(contains)).Id;

    [Fact]
    public void Dev_contracts_are_registered()
    {
        var b = Runtime().Bundle;
        Assert.True(b.IsRegistered(Kinds.ReadRepo));
        Assert.True(b.IsRegistered(Kinds.ModifyCode));
        Assert.True(b.IsRegistered(Kinds.RunCommand));
        Assert.True(b.IsRegistered(Kinds.OpenPullRequest));
    }

    [Fact]
    public void Allowlisted_command_confirms_but_non_allowlisted_is_blocked()
    {
        var r = Runtime().Run(DevPrompt, Workspace.CreateDemo());

        var test = r.Policy.Single(p => p.NodeId == Id(r, Kinds.RunCommand, "dotnet test"));
        Assert.Equal("Confirm", test.Decision);
        Assert.Contains("pol-command-allowlisted", test.TriggeredRules);

        var deploy = r.Policy.Single(p => p.NodeId == Id(r, Kinds.RunCommand, "deploy"));
        Assert.Equal("Block", deploy.Decision);                       // shell blocked by default
        Assert.Contains("pol-command-not-allowlisted", deploy.TriggeredRules);
    }

    [Fact]
    public void Injected_shell_command_from_repo_content_is_blocked()
    {
        var ws = Workspace.CreateDemo();
        var r = Runtime().Run(DevPrompt, ws);

        var injected = r.Nodes.Single(n => n.TrustSource == "RetrievedContent");
        Assert.Equal(Kinds.RunCommand, injected.Type);
        Assert.Equal("Blocked", injected.Status);
        var decision = r.Policy.Single(p => p.NodeId == injected.Id);
        Assert.Contains("pol-zero-trust-side-effect", decision.TriggeredRules);

        Assert.Empty(ws.Repo.RanCommands);                            // nothing ran
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Fact]
    public void Nothing_is_pushed_and_no_secret_leaks_even_when_everything_is_approved()
    {
        var rt = Runtime();
        var probe = rt.Run(DevPrompt, Workspace.CreateDemo());
        var approveAll = probe.Nodes.Select(n => n.Id).ToHashSet();   // approve every node, incl. injected

        var ws = Workspace.CreateDemo();
        var r = rt.Run(DevPrompt, ws, approveAll);

        // The injected command and the non-allowlisted deploy stay blocked regardless of approval.
        Assert.Empty(ws.Repo.PushedRefs);
        Assert.All(ws.Repo.StagedEdits, e => Assert.False(e.Pushed));
        Assert.All(ws.Repo.DraftPRs, p => Assert.False(p.Pushed));
        Assert.DoesNotContain(ws.Repo.RanCommands, c => !ws.Repo.IsAllowed(c));   // no blocked command ran
        var secrets = ws.Repo.SecretValues.ToList();
        Assert.DoesNotContain(ws.Repo.DraftPRs, p => secrets.Any(s => p.Body.Contains(s)));
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Fact]
    public void Approving_the_allowlisted_test_command_runs_it()
    {
        var rt = Runtime();
        var testId = Id(rt.Run(DevPrompt, Workspace.CreateDemo()), Kinds.RunCommand, "dotnet test");

        var ws = Workspace.CreateDemo();
        var r = rt.Run(DevPrompt, ws, new HashSet<string> { testId });

        Assert.Contains("dotnet test", ws.Repo.RanCommands);
        Assert.All(ws.Repo.RanCommands, c => Assert.True(ws.Repo.IsAllowed(c)));
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }
}
