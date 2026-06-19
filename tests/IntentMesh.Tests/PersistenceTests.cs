using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Tests for run/audit persistence + replay. An audit you can't store and re-verify later isn't an
/// audit — these prove a run round-trips to disk, the signature verifies, the run is deterministically
/// reproducible, and tampering with a persisted artifact is detected.
/// </summary>
public sealed class PersistenceTests
{
    private const string Prompt =
        "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";

    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "im-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Save_then_load_round_trips_and_verifies()
    {
        var root = TempRoot();
        try
        {
            var bundle = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
            var store = new FileRunArtifactStore(root);

            var id = store.Save(bundle);
            Assert.Contains(id, store.List());

            var loaded = store.Load(id);
            Assert.Equal(bundle.BundleSignature, loaded.BundleSignature);
            Assert.True(TraceBundleBuilder.VerifySignature(loaded));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Save_writes_the_five_artifacts_plus_the_bundle()
    {
        var root = TempRoot();
        try
        {
            var bundle = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
            var id = new FileRunArtifactStore(root).Save(bundle);
            var dir = Path.Combine(root, id);

            foreach (var name in new[] { "bundle.json", "intent.graph.json", "policy.decisions.json",
                "execution.trace.json", "verification.report.json", "audit.signed.json" })
                Assert.True(File.Exists(Path.Combine(dir, name)), $"missing {name}");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Replay_reproduces_a_persisted_run_deterministically()
    {
        var root = TempRoot();
        try
        {
            var rt = Runtime();
            var bundle = TraceBundleBuilder.From(rt.Run(Prompt, Workspace.CreateDemo()));
            var store = new FileRunArtifactStore(root);
            var id = store.Save(bundle);

            var replay = RunReplay.Reproduce(rt, Workspace.CreateDemo(), store.Load(id));
            Assert.True(replay.SignatureVerified, "stored signature should verify");
            Assert.True(replay.Reproduced, "re-running the same prompt+approvals should be byte-identical");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Tampering_with_a_persisted_artifact_is_detected_on_load()
    {
        var root = TempRoot();
        try
        {
            var bundle = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
            var store = new FileRunArtifactStore(root);
            var id = store.Save(bundle);

            // Tamper with the persisted bundle: flip a status token in the combined file.
            var path = Path.Combine(root, id, FileRunArtifactStore.BundleFile);
            File.WriteAllText(path, File.ReadAllText(path).Replace("NeedsConfirmation", "Executed"));

            var tampered = store.Load(id);
            Assert.False(TraceBundleBuilder.VerifySignature(tampered), "tampered bundle must not verify");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Verify_artifacts_detects_tampering_with_a_derived_split_file()
    {
        var root = TempRoot();
        try
        {
            var bundle = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
            var store = new FileRunArtifactStore(root);
            var id = store.Save(bundle);

            Assert.True(store.VerifyArtifacts(id), "intact run should verify (bundle + all split files)");

            // Tamper a DERIVED split file (not bundle.json) — full integrity check must catch it.
            var split = Path.Combine(root, id, "intent.graph.json");
            File.WriteAllText(split, File.ReadAllText(split).Replace("NeedsConfirmation", "Executed"));
            Assert.False(store.VerifyArtifacts(id), "a tampered split export must fail the integrity check");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Run_id_is_deterministic_from_the_signature()
    {
        var a = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
        var b = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
        Assert.Equal(FileRunArtifactStore.RunIdOf(a), FileRunArtifactStore.RunIdOf(b));
    }
}
