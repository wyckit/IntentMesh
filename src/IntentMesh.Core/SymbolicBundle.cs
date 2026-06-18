using IntentMesh.Tlm;

namespace IntentMesh.Core;

/// <summary>Metadata for one registered action contract, loaded from im-action-contracts.</summary>
public sealed record ContractInfo(
    string Kind, string Label, string Risk, string SideEffect,
    bool RequiresConfirmation, IReadOnlyList<string> Fields, IReadOnlyList<string> Postconditions);

/// <summary>One cue: trigger synonyms -> a signal. Loaded from im-nl-vocabulary.</summary>
public sealed record CueInfo(string Id, IReadOnlyList<string> Triggers, string Signal);

/// <summary>One policy rule (id + rule + action). Loaded from im-policy-rules.</summary>
public sealed record RuleInfo(string Id, string Rule, string Action);

/// <summary>One emergent skill (a reusable composition of action kinds). Loaded from im-skills.</summary>
public sealed record SkillInfo(string Id, string Label, string Status, string Risk,
    IReadOnlyList<string> AllowedTools, IReadOnlyList<string> Composition);

/// <summary>One lifecycle state in the governed skill pipeline. Loaded from im-skills.</summary>
public sealed record LifecycleStateInfo(string Id, string Label, int Order);

/// <summary>
/// Loads the compiled im-* TLM bundle once and exposes it as the symbolic layer the pipeline
/// reads: the contract registry (Translation-Drift guard), the cue book (the resolver's
/// vocabulary), and the policy book (the gate's citable rules). "The TLM is the model."
/// </summary>
public sealed class SymbolicBundle
{
    public IReadOnlyDictionary<string, ContractInfo> Contracts { get; }
    public IReadOnlyList<CueInfo> Cues { get; }
    public IReadOnlyList<RuleInfo> Rules { get; }
    public IReadOnlyList<string> PostconditionLabels { get; }
    public IReadOnlyList<SkillInfo> Skills { get; }
    public IReadOnlyList<LifecycleStateInfo> Lifecycle { get; }
    /// <summary>action kind -> the capability its tool requires (from im-tools).</summary>
    public IReadOnlyDictionary<string, string> Capabilities { get; }
    /// <summary>Every capability declared in the bundle (the default granted set).</summary>
    public IReadOnlySet<string> AllCapabilities { get; }

    private SymbolicBundle(Dictionary<string, ContractInfo> contracts, List<CueInfo> cues,
        List<RuleInfo> rules, List<string> postLabels, List<SkillInfo> skills, List<LifecycleStateInfo> lifecycle,
        Dictionary<string, string> capabilities)
    {
        Contracts = contracts; Cues = cues; Rules = rules; PostconditionLabels = postLabels;
        Skills = skills; Lifecycle = lifecycle; Capabilities = capabilities;
        AllCapabilities = capabilities.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsRegistered(string kind) => Contracts.ContainsKey(kind);

    public static SymbolicBundle Load(string compiledDir)
    {
        var compiler = new TlmCompiler();
        var packages = Directory.GetFiles(compiledDir, "im-*.tlmz")
            .Select(f => compiler.Deserialize(File.ReadAllBytes(f)))
            .ToList();

        var contracts = new Dictionary<string, ContractInfo>(StringComparer.OrdinalIgnoreCase);
        var cues = new List<CueInfo>();
        var rules = new List<RuleInfo>();
        var postLabels = new List<string>();
        var skills = new List<SkillInfo>();
        var lifecycle = new List<LifecycleStateInfo>();
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in packages)
        {
            foreach (var c in pkg.Concepts)
            {
                string P(string k) => c.Properties.GetValueOrDefault(k, "");
                switch (c.Category)
                {
                    case "ToolAdapter":
                        var capName = P("Capability");
                        if (capName.Length > 0)
                            foreach (var k in Split(P("Consumes")))
                                capabilities[k] = capName;
                        break;
                    case "ActionContract":
                        contracts[c.Id] = new ContractInfo(
                            c.Id, c.Label, P("Risk"), P("SideEffect"),
                            bool.TryParse(P("RequiresConfirmation"), out var rc) && rc,
                            Split(P("Fields")), Split(P("Postconditions")));
                        break;
                    case "Postcondition":
                        postLabels.Add(c.Label);
                        break;
                    case "Skill":
                        skills.Add(new SkillInfo(c.Id, c.Label, P("Status"), P("Risk"),
                            Split(P("AllowedTools")), Split(P("Composition"))));
                        break;
                    case "LifecycleState":
                        lifecycle.Add(new LifecycleStateInfo(c.Id, c.Label,
                            int.TryParse(P("Order"), out var ord) ? ord : 0));
                        break;
                }
            }
            foreach (var cue in pkg.Cues)
                cues.Add(new CueInfo(cue.Id,
                    cue.Trigger.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
                    cue.Signal));
            foreach (var p in pkg.Policies)
                rules.Add(new RuleInfo(p.Id, p.Rule, p.Action));
        }

        if (contracts.Count == 0)
            throw new InvalidOperationException(
                $"No action contracts found in '{compiledDir}'. Run the tlm CLI: author + compile all.");

        lifecycle.Sort((a, b) => a.Order.CompareTo(b.Order));
        return new SymbolicBundle(contracts, cues, rules, postLabels, skills, lifecycle, capabilities);
    }

    private static List<string> Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>Finds the compiled TLM bundle by walking up for a `dataset/compiled` directory.</summary>
public static class DatasetLocator
{
    public static string FindCompiledDir(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "dataset", "compiled");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "im-*.tlmz").Length > 0)
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate dataset/compiled with im-*.tlmz. Run `tlm author` + `tlm compile all`.");
    }
}
