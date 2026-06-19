namespace IntentMesh.Core;

/// <summary>
/// The public Runtime SDK surface — a thin, stable facade over the pipeline so a builder can route
/// an existing agent's actions through IntentMesh before any tool runs:
///
///   propose → compileGraph → evaluatePolicy → executeTypedAction → verify → exportAudit
///
/// <see cref="Propose"/> exposes the proposal seam (plug in an LLM). The middle four steps —
/// compile the graph, evaluate policy, execute typed actions, verify postconditions — are
/// deliberately coupled inside <see cref="Run"/>: you cannot execute without passing the gate.
/// <see cref="Bundle"/> / <see cref="SignAudit"/> export the audit.
/// </summary>
public sealed class IntentMeshSdk
{
    private readonly IntentMeshRuntime _runtime;
    private readonly IIntentProposer _proposer;
    private readonly SymbolicBundle _bundle;

    public IntentMeshSdk(IntentMeshRuntime runtime, IIntentProposer? proposer = null)
    {
        _runtime = runtime;
        _bundle = runtime.Bundle;
        _proposer = proposer ?? new IntentResolver(runtime.Bundle);
    }

    /// <summary>Load the SDK with the default (rule-based) proposer.</summary>
    public static IntentMeshSdk Load(string? compiledDir = null)
        => new(IntentMeshRuntime.Load(compiledDir));

    /// <summary>Wrap an existing agent: supply your own proposer (e.g., an LLM adapter). Its
    /// proposed actions still become typed intent that the gate validates — it never gains authority.</summary>
    public static IntentMeshSdk WithProposer(IIntentProposer proposer, string? compiledDir = null)
    {
        var bundle = SymbolicBundle.Load(compiledDir ?? DatasetLocator.FindCompiledDir());
        return new IntentMeshSdk(new IntentMeshRuntime(bundle, proposer), proposer);
    }

    /// <summary>Step 1 — propose. Language → typed, registry-bounded intent candidates (untrusted).</summary>
    public ProposedPlan Propose(string prompt, Workspace ws) => _proposer.Propose(prompt, ws);

    /// <summary>Steps 2–5 — compile the intent graph, evaluate policy, execute only validated typed
    /// actions, and verify postconditions. The gate is mandatory; nothing executes that it blocks.</summary>
    public RunResult Run(string prompt, Workspace ws, IReadOnlySet<string>? approvals = null)
        => _runtime.Run(prompt, ws, approvals ?? new HashSet<string>());

    /// <summary>Step 6 — export the signed trace bundle (all five artifacts).</summary>
    public TraceBundle Bundle(string prompt, Workspace ws, IReadOnlySet<string>? approvals = null)
    {
        var appr = approvals ?? new HashSet<string>();
        return TraceBundleBuilder.From(Run(prompt, ws, appr), appr.ToList());
    }

    /// <summary>Export the signed bundle for an ALREADY-completed run — no re-run (use this when you
    /// already hold the <see cref="RunResult"/> from <see cref="Run"/>).</summary>
    public TraceBundle Bundle(RunResult result, IReadOnlyList<string>? approvals = null)
        => TraceBundleBuilder.From(result, approvals ?? Array.Empty<string>());

    /// <summary>Export the tamper-evident signed audit for a completed run.</summary>
    public SignedAudit SignAudit(RunResult result) => AuditSigner.Sign(result);

    /// <summary>Step 7 — persist a run as a signed, content-addressed bundle into <paramref name="store"/>;
    /// returns the run id. The same surface a host uses for history, replay, and tamper-verification.</summary>
    public string Save(IRunArtifactStore store, RunResult result, IReadOnlyList<string>? approvals = null)
        => store.Save(Bundle(result, approvals));

    /// <summary>Replay a persisted run: re-verify its signature and re-run it deterministically through
    /// THIS sdk's runtime, checking it reproduces byte-for-byte. The audit-trust check an operator runs.</summary>
    public ReplayResult Replay(TraceBundle saved, Func<Workspace> freshWorkspace, byte[]? key = null)
        => RunReplay.Reproduce(_runtime, freshWorkspace(), saved, key);

    /// <summary>Rotation-aware replay: resolve the key the bundle recorded via <paramref name="keyProvider"/>
    /// (current + prior keys), so a run signed before a key rotation still verifies and reproduces
    /// byte-for-byte. Prefer this over the raw-key overload once keys may have rotated.</summary>
    public ReplayResult Replay(TraceBundle saved, Func<Workspace> freshWorkspace, IAuditKeyProvider keyProvider)
        => RunReplay.Reproduce(_runtime, freshWorkspace(), saved, keyProvider);

    /// <summary>The operator reasoning view: why a node is blocked, what awaits approval, and what
    /// granting every pending approval would change (a blocked node provably stays blocked).</summary>
    public RunExplanation Explain(string prompt, Func<Workspace> freshWorkspace, IReadOnlySet<string>? approvals = null)
        => RunExplain.Explain(_runtime, prompt, freshWorkspace, approvals);

    /// <summary>True iff the action kind is a registered typed contract (the Translation-Drift bound).</summary>
    public bool IsRegistered(string kind) => _bundle.IsRegistered(kind);

    /// <summary>The registered typed-contract kinds — the closed set a proposer may emit.</summary>
    public IReadOnlyCollection<string> RegisteredKinds => _bundle.Contracts.Keys.ToList();
}
