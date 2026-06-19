using System.Text.Json;

namespace IntentMesh.Core;

/// <summary>The difference between two policy rule sets — for reviewing what a bundle change did.</summary>
public sealed record PolicyRuleDiff(
    IReadOnlyList<RuleInfo> Added,
    IReadOnlyList<RuleInfo> Removed,
    IReadOnlyList<(RuleInfo Before, RuleInfo After)> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// Read-only views over the policy rule set (loaded from <c>im-policy-rules</c>). The PolicyGate
/// predicates stay authoritative in C#; this surfaces the generated symbolic citations (id + plain
/// rule + action) for review, diffing, and the Control Room's policy-evidence panel — no DSL.
/// </summary>
public static class PolicyCatalog
{
    public static IReadOnlyList<RuleInfo> Rules(SymbolicBundle bundle) => bundle.Rules;

    /// <summary>Look up the human-readable rule a decision cited (for "why-blocked" explanations).</summary>
    public static RuleInfo? Find(SymbolicBundle bundle, string ruleId)
        => bundle.Rules.FirstOrDefault(r => string.Equals(r.Id, ruleId, StringComparison.Ordinal));

    /// <summary>Diff two rule sets by id: rules added, removed, or whose text/action changed.</summary>
    public static PolicyRuleDiff Diff(IEnumerable<RuleInfo> before, IEnumerable<RuleInfo> after)
    {
        var b = before.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var a = after.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var added = a.Values.Where(r => !b.ContainsKey(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var removed = b.Values.Where(r => !a.ContainsKey(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var changed = a.Values
            .Where(r => b.TryGetValue(r.Id, out var ob) && (ob.Rule != r.Rule || ob.Action != r.Action))
            .Select(r => (b[r.Id], r))
            .OrderBy(t => t.Item2.Id, StringComparer.Ordinal).ToList();
        return new PolicyRuleDiff(added, removed, changed);
    }
}

/// <summary>
/// One expected policy outcome: running <see cref="Prompt"/> must produce a node of
/// <see cref="ExpectKind"/> whose policy decision is <see cref="ExpectDecision"/> and whose triggered
/// rules include <see cref="ExpectRule"/> (when set). The fixture set is the testable, versionable
/// spec of "what each rule is supposed to catch".
/// </summary>
public sealed record PolicyFixture(string Id, string Prompt, string ExpectKind, string ExpectDecision, string ExpectRule = "");

public sealed record PolicyFixtureResult(PolicyFixture Fixture, bool Pass, string Detail);

/// <summary>Loads and runs policy fixtures against the real pipeline (rule-based proposer + demo
/// workspace), so new rules can be authored, tested, and explained as data.</summary>
public static class PolicyFixtures
{
    public static IReadOnlyList<PolicyFixture> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<PolicyFixture>();
        if (doc.RootElement.TryGetProperty("fixtures", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var f in arr.EnumerateArray())
                list.Add(new PolicyFixture(
                    Str(f, "id"), Str(f, "prompt"), Str(f, "expectKind"), Str(f, "expectDecision"), Str(f, "expectRule")));
        return list;
    }

    /// <summary>The canonical fixtures shipped alongside the bundle (dataset/policy-fixtures.json).</summary>
    public static string DefaultPath()
        => Path.Combine(Path.GetDirectoryName(DatasetLocator.FindCompiledDir())!, "policy-fixtures.json");

    public static PolicyFixtureResult Run(IntentMeshRuntime runtime, PolicyFixture f)
    {
        var result = runtime.Run(f.Prompt, Workspace.CreateDemo());
        var match = result.Nodes.FirstOrDefault(n => n.Type == f.ExpectKind
            && result.Policy.Any(p => p.NodeId == n.Id && p.Decision == f.ExpectDecision));
        if (match is null)
            return new(f, false, $"no '{f.ExpectKind}' node with decision '{f.ExpectDecision}'");
        var pol = result.Policy.First(p => p.NodeId == match.Id);
        bool ruleOk = string.IsNullOrEmpty(f.ExpectRule) || pol.TriggeredRules.Contains(f.ExpectRule);
        return new(f, ruleOk, ruleOk ? $"{f.ExpectDecision} via {string.Join(",", pol.TriggeredRules)}"
                                     : $"expected rule '{f.ExpectRule}', got [{string.Join(",", pol.TriggeredRules)}]");
    }

    public static IReadOnlyList<PolicyFixtureResult> RunAll(IntentMeshRuntime runtime, IEnumerable<PolicyFixture> fixtures)
        => fixtures.Select(f => Run(runtime, f)).ToList();

    private static string Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
