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

    private SymbolicBundle(Dictionary<string, ContractInfo> contracts, List<CueInfo> cues,
        List<RuleInfo> rules, List<string> postLabels)
    { Contracts = contracts; Cues = cues; Rules = rules; PostconditionLabels = postLabels; }

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

        foreach (var pkg in packages)
        {
            foreach (var c in pkg.Concepts)
            {
                if (c.Category == "ActionContract")
                {
                    string P(string k) => c.Properties.GetValueOrDefault(k, "");
                    contracts[c.Id] = new ContractInfo(
                        c.Id, c.Label, P("Risk"), P("SideEffect"),
                        bool.TryParse(P("RequiresConfirmation"), out var rc) && rc,
                        Split(P("Fields")), Split(P("Postconditions")));
                }
                else if (c.Category == "Postcondition")
                    postLabels.Add(c.Label);
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

        return new SymbolicBundle(contracts, cues, rules, postLabels);
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
