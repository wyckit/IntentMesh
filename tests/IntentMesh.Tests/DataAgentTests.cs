using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// v0.4 data-agent demo: NL becomes a typed query plan (AST) that is validated before execution.
/// A destructive op is blocked by the read-only role (even for the user); an injected "drop the
/// table" from data is a proposed AST node that fails validation; no sensitive column is exposed.
/// </summary>
public sealed class DataAgentTests
{
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();
    private const string DataPrompt = "Summarize signups by plan from the analytics database, delete old records, and email the client a report.";

    private static string DeleteId(RunResult r) => r.Nodes.First(n => n.Type == Kinds.BuildQueryPlan && n.Label.Contains("delete")).Id;

    [Fact]
    public void Data_contracts_are_registered()
    {
        var b = Runtime().Bundle;
        Assert.True(b.IsRegistered(Kinds.BuildQueryPlan));
        Assert.True(b.IsRegistered(Kinds.RunQuery));
    }

    [Fact]
    public void Readonly_role_allows_aggregate_but_blocks_delete()
    {
        var r = Runtime().Run(DataPrompt, Workspace.CreateDemo());

        var agg = r.Policy.Single(p => p.NodeId == r.Nodes.First(n => n.Type == Kinds.BuildQueryPlan && n.Label.Contains("signups by plan")).Id);
        Assert.Equal("Allow", agg.Decision);

        var del = r.Policy.Single(p => p.NodeId == DeleteId(r));
        Assert.Equal("Block", del.Decision);                       // read-only role blocks it, even though the user asked
        Assert.Contains("pol-query-readonly", del.TriggeredRules);
    }

    [Fact]
    public void Injected_drop_table_from_data_is_a_proposed_ast_node_that_fails_validation()
    {
        var ws = Workspace.CreateDemo();
        var r = Runtime().Run(DataPrompt, ws);

        var injected = r.Nodes.Single(n => n.TrustSource == "RetrievedContent");
        Assert.Equal(Kinds.BuildQueryPlan, injected.Type);
        Assert.Equal("Blocked", injected.Status);
        var decision = r.Policy.Single(p => p.NodeId == injected.Id);
        Assert.Contains("pol-query-untrusted", decision.TriggeredRules);

        Assert.Empty(ws.Db.Mutations);                             // nothing mutated
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Fact]
    public void No_sensitive_column_is_exposed_in_the_report_even_when_everything_is_approved()
    {
        var rt = Runtime();
        var probe = rt.Run(DataPrompt, Workspace.CreateDemo());
        var ws = Workspace.CreateDemo();
        var r = rt.Run(DataPrompt, ws, probe.Nodes.Select(n => n.Id).ToHashSet());

        var emails = ws.Db.Tables.SelectMany(t => t.Rows.Select(row => row.Length > 1 ? row[1] : "")).Where(v => v.Contains('@'));
        Assert.All(ws.Drafts, d => Assert.DoesNotContain(emails, e => d.Body.Contains(e)));
        Assert.Empty(ws.Db.Mutations);
        Assert.All(r.Verification, v => Assert.True(v.Pass));
    }

    [Theory]
    [InlineData("delete")]   // lowercase variant of a destructive op
    [InlineData("DROP")]
    [InlineData("Update")]
    [InlineData("upsert")]   // unknown write op — default-deny
    public void Non_read_query_ops_are_blocked_under_a_read_only_role(string op)
    {
        var bundle = SymbolicBundle.Load(DatasetLocator.FindCompiledDir());
        var gate = new PolicyGate(bundle);
        var ws = Workspace.CreateDemo();   // db.Role == "read-only"
        var ctx = new PolicyContext(ws, new HashSet<string>(), bundle.AllCapabilities, bundle.Capabilities);

        var node = new IntentNode
        {
            Id = "q", Type = Kinds.BuildQueryPlan, Label = "plan",
            Action = new BuildQueryPlanAction(op, "signups", "x", 10),
            TrustSource = TrustSource.User, Status = NodeStatus.Resolved,
        };
        var d = gate.Evaluate(node, ctx);
        Assert.Equal(Decision.Block, d.Decision);
        Assert.Contains("pol-query-readonly", d.TriggeredRules);
    }

    [Fact]
    public void A_direct_run_query_is_validated_by_the_data_policy_not_generic_allow()
    {
        var bundle = SymbolicBundle.Load(DatasetLocator.FindCompiledDir());
        var gate = new PolicyGate(bundle);
        var ws = Workspace.CreateDemo();
        var ctx = new PolicyContext(ws, new HashSet<string>(), bundle.AllCapabilities, bundle.Capabilities);

        // Untrusted retrieved content may not originate a direct query (a read is side-effect "none", so
        // the zero-trust side-effect rule alone wouldn't catch it — this branch must).
        var injected = new IntentNode
        {
            Id = "q",
            Type = Kinds.RunQuery,
            Label = "run query",
            Action = new RunQueryAction("signups", "dump signups"),
            TrustSource = TrustSource.RetrievedContent,
            Status = NodeStatus.Resolved,
        };
        var d = gate.Evaluate(injected, ctx);
        Assert.Equal(Decision.Block, d.Decision);
        Assert.Contains("pol-query-untrusted", d.TriggeredRules);

        // A direct user query to a nonexistent table is blocked too (validated like a plan, not allowed).
        var missing = new IntentNode
        {
            Id = "q2",
            Type = Kinds.RunQuery,
            Label = "run query",
            Action = new RunQueryAction("nope_table", "x"),
            TrustSource = TrustSource.User,
            Status = NodeStatus.Resolved,
        };
        Assert.Equal(Decision.Block, gate.Evaluate(missing, ctx).Decision);
    }
}
