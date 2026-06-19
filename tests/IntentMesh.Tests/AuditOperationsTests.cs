using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Audit operations: key rotation (a key change must not invalidate already-signed audits), run
/// retention/archival, and the operator history summary. The tamper-verification path itself
/// (VerifyArtifacts, RunReplay) is covered in PersistenceTests.
/// </summary>
public sealed class AuditOperationsTests
{
    private const string Prompt =
        "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";

    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();
    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "im-auditops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Rotating_the_signing_key_does_not_invalidate_audits_signed_under_the_old_key()
    {
        var result = Runtime().Run(Prompt, Workspace.CreateDemo());
        var keyA = Key(0xA1);

        var signed = AuditSigner.Sign(result, new RotatingAuditKeyProvider("k-2026a", keyA));
        Assert.Equal("k-2026a", signed.KeyId);

        // Rotate: new current key k-2026b, with k-2026a retained as a prior key.
        var rotated = new RotatingAuditKeyProvider("k-2026b", Key(0xB2),
            new Dictionary<string, byte[]> { ["k-2026a"] = keyA });
        Assert.True(AuditSigner.Verify(signed, rotated), "audit signed under the prior key must still verify after rotation");

        // A provider that no longer holds the old key fails closed (can't forge, can't silently pass).
        var lostOldKey = new RotatingAuditKeyProvider("k-2026b", Key(0xB2));
        Assert.False(AuditSigner.Verify(signed, lostOldKey));
    }

    [Fact]
    public void A_weak_rotation_key_is_rejected_at_construction()
        => Assert.Throws<InvalidOperationException>(() => new RotatingAuditKeyProvider("k", new byte[] { 1, 2, 3 }));

    [Fact]
    public void A_persisted_run_still_verifies_and_replays_after_the_signing_key_rotates()
    {
        var root = TempRoot();
        try
        {
            var rt = Runtime();
            var keyA = Key(0xA1);

            // Persist a run SIGNED UNDER key A (records KeyId "k-2026a" on the bundle).
            var store = new FileRunArtifactStore(root);
            var signProvider = new RotatingAuditKeyProvider("k-2026a", keyA);
            var runId = store.Save(TraceBundleBuilder.From(rt.Run(Prompt, Workspace.CreateDemo()), null, signProvider));
            Assert.Equal("k-2026a", store.Load(runId).KeyId);

            // Rotate: current key is now k-2026b; k-2026a retained as a prior key.
            var rotated = new RotatingAuditKeyProvider("k-2026b", Key(0xB2),
                new Dictionary<string, byte[]> { ["k-2026a"] = keyA });

            // The whole bundle path must still verify + reproduce under the rotated provider.
            Assert.True(store.VerifyArtifacts(runId, rotated), "old run's artifacts must verify after rotation");
            var replay = RunReplay.Reproduce(rt, Workspace.CreateDemo(), store.Load(runId), rotated);
            Assert.True(replay.SignatureVerified);
            Assert.True(replay.Reproduced, "replay must reproduce byte-for-byte under the recorded (prior) key");

            // A provider that lost the old key cannot verify or reproduce it (fail-closed, no silent pass).
            var lostOldKey = new RotatingAuditKeyProvider("k-2026b", Key(0xB2));
            Assert.False(store.VerifyArtifacts(runId, lostOldKey));
            Assert.False(RunReplay.Reproduce(rt, Workspace.CreateDemo(), store.Load(runId), lostOldKey).Reproduced);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_pre_KeyId_bundle_signed_with_a_real_key_still_verifies_via_the_audit_keyid()
    {
        var realKey = Key(0xC3);
        var provider = new RotatingAuditKeyProvider("k-real", realKey);
        var bundle = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()), null, provider);
        Assert.Equal("k-real", bundle.KeyId);
        Assert.Equal("k-real", bundle.SignedAudit.KeyId);

        // Simulate a bundle persisted BEFORE the bundle-level KeyId field existed: the field
        // deserializes to the demo default, but SignedAudit.KeyId still carries the real signing key.
        var legacy = bundle with { KeyId = AuditSigner.DemoKeyId };

        // It must still verify + replay under a provider holding the real key (fallback to SignedAudit.KeyId).
        Assert.True(TraceBundleBuilder.VerifySignature(legacy, provider));
        var replay = RunReplay.Reproduce(Runtime(), Workspace.CreateDemo(), legacy, provider);
        Assert.True(replay.SignatureVerified);
        Assert.True(replay.Reproduced);

        // A provider that does NOT hold the real key still fails closed — no silent demo-key pass.
        Assert.False(TraceBundleBuilder.VerifySignature(legacy, new RotatingAuditKeyProvider("k-other", Key(0xD4))));
    }

    [Fact]
    public void Retention_prune_archives_older_runs_and_keeps_the_newest()
    {
        var root = TempRoot();
        try
        {
            var rt = Runtime();
            var store = new FileRunArtifactStore(root);
            foreach (var p in new[] { "Read my calendar for Friday.", "Clean up my downloads.", Prompt })
                store.Save(TraceBundleBuilder.From(rt.Run(p, Workspace.CreateDemo())));

            Assert.Equal(3, store.List().Count);

            var archived = store.Prune(keepNewest: 1);
            Assert.Equal(2, archived.Count);
            Assert.Single(store.List());                                  // .archive is not listed
            Assert.True(Directory.Exists(Path.Combine(root, ".archive"))); // archived runs preserved on disk
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Run_summaries_carry_prompt_decision_counts_and_signing_key()
    {
        var root = TempRoot();
        try
        {
            var store = new FileRunArtifactStore(root);
            store.Save(TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo())));

            var summary = Assert.Single(store.ListSummaries());
            Assert.Equal(Prompt, summary.Prompt);
            Assert.True(summary.Total > 0);
            Assert.Contains("INSECURE", summary.KeyId);   // demo key in tests; production records its real id
        }
        finally { Directory.Delete(root, true); }
    }
}
