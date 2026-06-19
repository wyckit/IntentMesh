using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Policy authoring is real: the shipped fixtures (dataset/policy-fixtures.json) are the testable,
/// versionable spec of what each rule catches, and policy diffing surfaces what a bundle change did.
/// </summary>
public sealed class PolicyFixtureTests
{
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();

    [Fact]
    public void Every_shipped_policy_fixture_passes_against_the_current_bundle()
    {
        var fixtures = PolicyFixtures.Load(PolicyFixtures.DefaultPath());
        Assert.NotEmpty(fixtures);

        var results = PolicyFixtures.RunAll(Runtime(), fixtures);
        var failures = results.Where(r => !r.Pass).Select(r => $"{r.Fixture.Id}: {r.Detail}").ToList();
        Assert.True(failures.Count == 0, "failing policy fixtures:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_fixtures_expected_rule_exists_in_the_policy_catalog()
    {
        var bundle = SymbolicBundle.Load(DatasetLocator.FindCompiledDir());
        foreach (var f in PolicyFixtures.Load(PolicyFixtures.DefaultPath()).Where(f => f.ExpectRule.Length > 0))
            Assert.True(PolicyCatalog.Find(bundle, f.ExpectRule) is not null,
                $"fixture '{f.Id}' cites rule '{f.ExpectRule}' which is not in im-policy-rules");
    }

    [Fact]
    public void Policy_diff_reports_added_removed_and_changed_rules()
    {
        var baseRules = new[]
        {
            new RuleInfo("pol-a", "condition a", "block"),
            new RuleInfo("pol-b", "condition b", "confirm"),
        };
        var changed = new[]
        {
            new RuleInfo("pol-a", "condition a", "block"),                 // unchanged
            new RuleInfo("pol-b", "condition b", "allow"),                 // action changed
            new RuleInfo("pol-c", "condition c", "block"),                 // added
        };

        var diff = PolicyCatalog.Diff(baseRules, changed);
        Assert.False(diff.IsEmpty);
        Assert.Equal("pol-c", Assert.Single(diff.Added).Id);
        Assert.Equal("pol-b", Assert.Single(diff.Changed).After.Id);
        Assert.Empty(diff.Removed);   // nothing dropped here
    }

    [Fact]
    public void A_clean_diff_against_itself_is_empty()
    {
        var rules = SymbolicBundle.Load(DatasetLocator.FindCompiledDir()).Rules;
        Assert.True(PolicyCatalog.Diff(rules, rules).IsEmpty);
    }
}
