namespace IntentMesh.Core;

/// <summary>
/// Persistence for run/audit artifacts. A run isn't auditable if you can't store it and re-verify it
/// later — this seam is the storage boundary. File-based first; a DB/blob sink is a drop-in
/// alternative behind the same interface.
/// </summary>
public interface IRunArtifactStore
{
    /// <summary>Persist a signed trace bundle; returns the run id used to key it.</summary>
    string Save(TraceBundle bundle);
    /// <summary>Load a previously-persisted bundle by run id.</summary>
    TraceBundle Load(string runId);
    /// <summary>The run ids currently stored.</summary>
    IReadOnlyList<string> List();
    /// <summary>Full on-disk integrity check: the signed bundle verifies AND every derived split
    /// artifact byte-matches the re-derived bundle (catches tampering with any inspectable export).</summary>
    bool VerifyArtifacts(string runId, byte[]? key = null);

    /// <summary>Rotation-aware integrity check: verify the bundle under the key its recorded id names
    /// (the provider supplies historical keys), so a run signed before a key rotation still verifies.</summary>
    bool VerifyArtifacts(string runId, IAuditKeyProvider provider);
}

/// <summary>
/// Writes each run to <c>{root}/runs/{runId}/</c> as the five split artifacts plus a combined
/// <c>bundle.json</c>. The run id is derived from the bundle signature, so it is deterministic:
/// the same prompt + approvals + key always lands at the same id, and a tampered artifact verifies
/// false on load.
/// </summary>
public sealed class FileRunArtifactStore : IRunArtifactStore
{
    public const string BundleFile = "bundle.json";
    private readonly string _root;

    public FileRunArtifactStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>Deterministic run id: the first 16 hex of the bundle signature.</summary>
    public static string RunIdOf(TraceBundle bundle)
        => bundle.BundleSignature[..Math.Min(16, bundle.BundleSignature.Length)];

    /// <summary>Persist the run. <c>bundle.json</c> is the canonical, signed artifact (verified on
    /// load/replay); the five split files (<c>intent.graph.json</c> … <c>audit.signed.json</c>) are
    /// <b>derived exports</b> of it for human inspection. Use <see cref="VerifyArtifacts"/> to confirm
    /// the split files still match the signed bundle.</summary>
    public string Save(TraceBundle bundle)
    {
        var id = RunIdOf(bundle);
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BundleFile), TraceBundleBuilder.ToJson(bundle));
        foreach (var (name, json) in TraceBundleBuilder.SplitFiles(bundle))   // derived exports
            File.WriteAllText(Path.Combine(dir, name), json);
        return id;
    }

    public TraceBundle Load(string runId)
    {
        var path = Path.Combine(_root, runId, BundleFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No persisted run '{runId}'.", path);
        return TraceBundleBuilder.FromJson(File.ReadAllText(path));
    }

    /// <summary>
    /// Full on-disk integrity check for a persisted run: the canonical <c>bundle.json</c> must
    /// signature-verify, AND each derived split file must byte-match what the signed bundle
    /// re-derives. Catches tampering with either the bundle or any split export.
    /// </summary>
    public bool VerifyArtifacts(string runId, byte[]? key = null)
        => VerifyArtifacts(runId, Load(runId), TraceBundleBuilder.VerifySignature(Load(runId), key));

    public bool VerifyArtifacts(string runId, IAuditKeyProvider provider)
        => VerifyArtifacts(runId, Load(runId), TraceBundleBuilder.VerifySignature(Load(runId), provider));

    private bool VerifyArtifacts(string runId, TraceBundle bundle, bool signatureValid)
    {
        if (!signatureValid) return false;
        var dir = Path.Combine(_root, runId);
        foreach (var (name, expected) in TraceBundleBuilder.SplitFiles(bundle))
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) || File.ReadAllText(path) != expected) return false;
        }
        return true;
    }

    public IReadOnlyList<string> List()
        => Directory.Exists(_root)
            ? Directory.GetDirectories(_root)
                .Select(d => Path.GetFileName(d)!)
                .Where(name => !name.StartsWith('.'))                 // skip .archive and other dot-dirs
                .OrderBy(x => x, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    /// <summary>Run summaries for an operator history view — loaded from each persisted bundle.json
    /// (corrupt/unreadable runs are skipped). Newest first by on-disk write time.</summary>
    public IReadOnlyList<RunSummary> ListSummaries()
    {
        var summaries = new List<(DateTime When, RunSummary Summary)>();
        foreach (var id in List())
        {
            try
            {
                var b = Load(id);
                var s = b.Summary;
                var when = File.GetLastWriteTimeUtc(Path.Combine(_root, id, BundleFile));
                summaries.Add((when, new RunSummary(id, b.Prompt, s.Total, s.Allowed, s.NeedsConfirmation,
                    s.Blocked, s.Executed, s.Verified, b.SignedAudit.KeyId, b.Approvals.Count)));
            }
            catch { /* skip a corrupt run rather than fail the whole listing */ }
        }
        return summaries.OrderByDescending(x => x.When).Select(x => x.Summary).ToList();
    }

    /// <summary>Move a run's artifacts to a <c>.archive/</c> subdirectory (preserving verifiability),
    /// so retention doesn't destroy the audit trail.</summary>
    public void Archive(string runId)
    {
        var src = Path.Combine(_root, runId);
        if (!Directory.Exists(src)) return;
        var archiveRoot = Path.Combine(_root, ".archive");
        Directory.CreateDirectory(archiveRoot);
        var dest = Path.Combine(archiveRoot, runId);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(src, dest);
    }

    /// <summary>Retention: keep the <paramref name="keepNewest"/> most-recent runs live and archive
    /// the rest (by on-disk write time). Returns the archived run ids.</summary>
    public IReadOnlyList<string> Prune(int keepNewest)
    {
        if (keepNewest < 0) throw new ArgumentOutOfRangeException(nameof(keepNewest));
        var ordered = List()
            .Select(id => (id, when: File.GetLastWriteTimeUtc(Path.Combine(_root, id, BundleFile))))
            .OrderByDescending(x => x.when)
            .Select(x => x.id)
            .ToList();
        var toArchive = ordered.Skip(keepNewest).ToList();
        foreach (var id in toArchive) Archive(id);
        return toArchive;
    }
}

/// <summary>A compact, inspectable summary of one persisted run — the row an operator history view
/// renders. Built from the persisted bundle (prompt, decision counts, signing key id, approvals).</summary>
public sealed record RunSummary(
    string RunId, string Prompt, int Total, int Allowed, int NeedsConfirmation,
    int Blocked, int Executed, int Verified, string KeyId, int ApprovalCount);

/// <summary>The outcome of replaying a persisted run.</summary>
public sealed record ReplayResult(bool SignatureVerified, bool Reproduced, string RecomputedSignature);

/// <summary>
/// Replays a persisted run: verifies the stored bundle's signature, then re-runs the same prompt +
/// approvals through the runtime and checks the recomputed bundle signature is byte-identical. A
/// failed signature means tampering; a non-reproduction means the run wasn't deterministic (or the
/// workspace/bundle differs). Both must hold for an audit to be trustworthy.
/// </summary>
public static class RunReplay
{
    public static ReplayResult Reproduce(IntentMeshRuntime runtime, Workspace freshWorkspace, TraceBundle saved, byte[]? key = null)
    {
        bool sigOk = TraceBundleBuilder.VerifySignature(saved, key);
        var approvals = saved.Approvals.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rerun = TraceBundleBuilder.From(runtime.Run(saved.Prompt, freshWorkspace, approvals), saved.Approvals.ToList(), key);
        return new ReplayResult(sigOk, rerun.BundleSignature == saved.BundleSignature, rerun.BundleSignature);
    }

    /// <summary>Rotation-aware replay: verify the saved bundle under the key its recorded id names, then
    /// re-sign the deterministic re-run under that SAME key — so reproduction stays byte-identical even
    /// after the current signing key has rotated. An unknown key id can't reproduce (fail-closed).</summary>
    public static ReplayResult Reproduce(IntentMeshRuntime runtime, Workspace freshWorkspace, TraceBundle saved, IAuditKeyProvider provider)
    {
        bool sigOk = TraceBundleBuilder.VerifySignature(saved, provider);
        var key = AuditSigner.ResolveKey(saved.KeyId, provider);
        if (key is null) return new ReplayResult(sigOk, Reproduced: false, RecomputedSignature: "");
        var approvals = saved.Approvals.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rerun = TraceBundleBuilder.From(runtime.Run(saved.Prompt, freshWorkspace, approvals),
            saved.Approvals.ToList(), new FixedKeyProvider(saved.KeyId, key));
        return new ReplayResult(sigOk, rerun.BundleSignature == saved.BundleSignature, rerun.BundleSignature);
    }
}
