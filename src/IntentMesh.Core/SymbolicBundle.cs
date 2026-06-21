using IntentMesh.Tlm;

namespace IntentMesh.Core;

/// <summary>Metadata for one registered action contract, loaded from im-action-contracts.</summary>
public sealed record ContractInfo(
    string Kind, string Label, string Risk, string SideEffect,
    bool RequiresConfirmation, IReadOnlyList<string> Fields, IReadOnlyList<string> RequiredFields, IReadOnlyList<string> Postconditions);

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
        => Build(Directory.GetFiles(compiledDir, "im-*.tlmz").Select(File.ReadAllBytes).ToList(), compiledDir);

    /// <summary>Load the bundle for a host that may not ship the dataset folder (e.g. a NuGet consumer):
    /// use <paramref name="compiledDir"/> if given, else a discovered <c>dataset/compiled</c>, else the
    /// im-*.tlmz EMBEDDED in this assembly. So <see cref="IntentMeshSdk.Load"/> works out of the box.</summary>
    public static SymbolicBundle LoadDefault(string? compiledDir = null)
    {
        if (compiledDir is not null) return Load(compiledDir);
        try { return Load(DatasetLocator.FindCompiledDir()); }
        catch (DirectoryNotFoundException) { }
        try { return Load(DatasetLocator.FindCompiledDir(AppContext.BaseDirectory)); }
        catch (DirectoryNotFoundException) { }
        return LoadEmbedded();
    }

    /// <summary>Load the bundle from the im-*.tlmz files embedded in IntentMesh.Core — the
    /// self-contained path that needs no dataset folder on disk.</summary>
    public static SymbolicBundle LoadEmbedded()
    {
        var asm = typeof(SymbolicBundle).Assembly;
        var names = asm.GetManifestResourceNames().Where(n => n.Contains("im-") && n.EndsWith(".tlmz")).ToList();
        var bytes = new List<byte[]>();
        foreach (var n in names)
        {
            using var s = asm.GetManifestResourceStream(n)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            bytes.Add(ms.ToArray());
        }
        if (bytes.Count == 0)
            throw new InvalidOperationException("No im-*.tlmz embedded in IntentMesh.Core. Build the package with the compiled bundle embedded.");
        return Build(bytes, "embedded:IntentMesh.Core");
    }

    private static SymbolicBundle Build(IReadOnlyList<byte[]> packageBytes, string source)
    {
        var compiler = new TlmCompiler();
        var packages = packageBytes.Select(compiler.Deserialize).ToList();

        var contracts = new Dictionary<string, ContractInfo>(StringComparer.OrdinalIgnoreCase);
        var cues = new List<CueInfo>();
        var rules = new List<RuleInfo>();
        var postLabels = new List<string>();
        var skills = new List<SkillInfo>();
        var lifecycle = new List<LifecycleStateInfo>();
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contractCaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // contract-declared (fallback)

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
                            Split(P("Fields")), Split(P("RequiredFields")), Split(P("Postconditions")));
                        // A contract may declare its own capability (e.g. an OpenAPI import that has no
                        // ToolAdapter concept). Register it so the capability gate enforces it at runtime.
                        // A ToolAdapter mapping, if present, takes precedence (don't clobber it).
                        var contractCap = P("Capability");
                        if (contractCap.Length > 0)
                            contractCaps[c.Id] = contractCap;
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

        // Contract-declared capabilities are a fallback: a ToolAdapter mapping wins (TryAdd keeps it),
        // but a contract with no adapter (e.g. an OpenAPI import) still gets its capability enforced.
        foreach (var kv in contractCaps) capabilities.TryAdd(kv.Key, kv.Value);

        if (contracts.Count == 0)
            throw new InvalidOperationException(
                $"No action contracts found in '{source}'. Run the tlm CLI: author + compile all.");

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
