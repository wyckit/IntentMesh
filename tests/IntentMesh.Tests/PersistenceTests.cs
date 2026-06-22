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
    public void Run_id_is_a_128_bit_content_address_and_resave_is_idempotent()
    {
        var root = TempRoot();
        try
        {
            var store = new FileRunArtifactStore(root);
            var bundle = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
            var id = store.Save(bundle);
            Assert.Equal(32, id.Length);                 // 128-bit content address (was 64-bit/16 hex)
            Assert.Equal(id, store.Save(bundle));         // same content → idempotent, no collision throw
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_signed_owner_sidecar_detects_tampering_under_verification()
    {
        var root = TempRoot();
        try
        {
            var store = new FileRunArtifactStore(root);
            var key = new FixedKeyProvider("test", System.Text.Encoding.UTF8.GetBytes("owner-test-key-0123456789abcdef"));
            var id = store.Save(TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo())));
            store.RecordOwner(id, new RunOwner("alice", "acme", 1700000000), key);

            Assert.NotNull(store.ReadOwner(id, key));                 // intact → verifies
            Assert.Equal("alice", store.ReadOwner(id, key)!.PrincipalId);

            var ownerPath = Path.Combine(root, id, FileRunArtifactStore.OwnerFile);
            File.WriteAllText(ownerPath, File.ReadAllText(ownerPath).Replace("alice", "mallory"));
            Assert.Null(store.ReadOwner(id, key));                    // tampered → rejected under verification
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:\\Windows")]
    [InlineData("%2e%2e")]
    [InlineData("")]
    [InlineData("zz-not-hex")]
    public void A_non_hex_or_traversal_run_id_is_rejected_not_resolved_as_a_path(string badId)
    {
        var root = TempRoot();
        try
        {
            var store = new FileRunArtifactStore(root);
            Assert.False(FileRunArtifactStore.IsValidRunId(badId));
            Assert.Throws<ArgumentException>(() => store.Load(badId));            // can't escape via Load
            Assert.Throws<ArgumentException>(() => store.VerifyArtifacts(badId)); // …or VerifyArtifacts
            Assert.Throws<ArgumentException>(() => store.Archive(badId));         // …or Archive
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_content_addressed_run_id_is_accepted()
        => Assert.True(FileRunArtifactStore.IsValidRunId("6df0037087a8c7f9"));

    [Fact]
    public void Replay_does_not_execute_when_the_signature_fails()
    {
        var saved = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
        var tampered = saved with { Prompt = saved.Prompt + " (tampered)" };   // breaks the bundle signature

        var replay = RunReplay.Reproduce(Runtime(), Workspace.CreateDemo(), tampered);

        Assert.False(replay.SignatureVerified);
        Assert.False(replay.Reproduced);
        Assert.Equal("", replay.RecomputedSignature);   // empty => the run was never re-executed (early return)
    }

    [Fact]
    public void A_signed_run_copied_under_a_different_id_is_not_intact()
    {
        var root = TempRoot();
        try
        {
            var store = new FileRunArtifactStore(root);
            var realId = store.Save(TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo())));
            Assert.True(store.VerifyArtifacts(realId));   // intact at its own content-address

            // Copy the run's files under a DIFFERENT valid-hex id.
            var otherId = new string('a', 16);
            var dst = Path.Combine(root, otherId);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(Path.Combine(root, realId)))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));

            // The bundle is byte-valid but its content-address is realId, not otherId → not intact here.
            Assert.False(store.VerifyArtifacts(otherId));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReadArtifact_serves_the_on_disk_file_and_reflects_tampering()
    {
        var root = TempRoot();
        try
        {
            var store = new FileRunArtifactStore(root);
            var runId = store.Save(TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo())));

            // Tamper a persisted split file on disk.
            var path = Path.Combine(root, runId, "policy.decisions.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("Confirm", "TAMPERED"));

            // The viewer reflects the tampered bytes (not a clean re-derivation) and /verify catches it.
            Assert.Contains("TAMPERED", store.ReadArtifact(runId, "policy.decisions.json"));
            Assert.False(store.VerifyArtifacts(runId));

            // A non-allowlisted artifact name (traversal attempt) is refused.
            Assert.Null(store.ReadArtifact(runId, "../bundle.json"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_runs_root_with_a_trailing_separator_still_saves_and_loads()
    {
        var root = TempRoot() + Path.DirectorySeparatorChar;   // e.g. "…\im-runs-xxxx\"
        try
        {
            var store = new FileRunArtifactStore(root);
            var runId = store.Save(TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo())));
            Assert.NotNull(store.Load(runId));                  // path guard must not reject the valid id
            Assert.True(store.VerifyArtifacts(runId));
        }
        finally { Directory.Delete(root, true); }
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
