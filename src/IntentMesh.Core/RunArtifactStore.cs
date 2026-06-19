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

    public string Save(TraceBundle bundle)
    {
        var id = RunIdOf(bundle);
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BundleFile), TraceBundleBuilder.ToJson(bundle));
        foreach (var (name, json) in TraceBundleBuilder.SplitFiles(bundle))
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

    public IReadOnlyList<string> List()
        => Directory.Exists(_root)
            ? Directory.GetDirectories(_root).Select(d => Path.GetFileName(d)!).OrderBy(x => x, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();
}

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
}
