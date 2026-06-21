using System.Text.Json.Nodes;
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
    public void A_signed_audit_with_a_tampered_transcript_fails_verification()
    {
        var result = Runtime().Run(Prompt, Workspace.CreateDemo());
        var provider = new RotatingAuditKeyProvider("k1", Key(0x5A));
        var signed = AuditSigner.Sign(result, provider);
        Assert.True(AuditSigner.Verify(signed, provider));   // intact transcript verifies

        // Edit the exported transcript (an audit event message) while leaving chainHash/signature/keyId
        // untouched — verification must now FAIL because the chain is recomputed from AuditJson.
        var node = JsonNode.Parse(signed.AuditJson)!;
        var events = node["audit"]!.AsArray();
        events[0]!["message"] = events[0]!["message"]!.GetValue<string>() + " FORGED";
        var tampered = signed with { AuditJson = node.ToJsonString() };
        Assert.False(AuditSigner.Verify(tampered, provider), "a tampered transcript must not verify");
    }

    [Fact]
    public void A_signed_audit_with_a_tampered_NON_event_field_fails_verification()
    {
        var result = Runtime().Run(Prompt, Workspace.CreateDemo());
        var provider = new RotatingAuditKeyProvider("k1", Key(0x5A));
        var signed = AuditSigner.Sign(result, provider);

        // Edit the prompt field of the exported transcript — NOT an audit event. The whole AuditJson is
        // bound to the signature, so this must fail even though the event chain is untouched.
        var node = JsonNode.Parse(signed.AuditJson)!;
        node["prompt"] = node["prompt"]!.GetValue<string>() + " TAMPERED";
        var tampered = signed with { AuditJson = node.ToJsonString() };
        Assert.False(AuditSigner.Verify(tampered, provider), "a non-event transcript edit must not verify");
    }

    [Fact]
    public void Bundle_signature_binds_top_level_fields()
    {
        var bundle = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()));
        Assert.True(TraceBundleBuilder.VerifySignature(bundle));

        // None of the displayed/replayed top-level fields can be changed without breaking the signature.
        Assert.False(TraceBundleBuilder.VerifySignature(bundle with { Prompt = bundle.Prompt + " X" }));
        Assert.False(TraceBundleBuilder.VerifySignature(bundle with { Approvals = new[] { "forged" } }));
        Assert.False(TraceBundleBuilder.VerifySignature(bundle with { Summary = bundle.Summary with { Blocked = 99 } }));
        Assert.False(TraceBundleBuilder.VerifySignature(bundle with { SchemaVersion = "9.9" }));
    }

    [Fact]
    public void Raw_and_fixed_key_signing_reject_a_sub_128_bit_key()
    {
        var weak = new byte[] { 1, 2, 3 };   // 24-bit — far below the 128-bit floor
        var result = Runtime().Run(Prompt, Workspace.CreateDemo());
        Assert.Throws<InvalidOperationException>(() => AuditSigner.Sign(result, weak));
        Assert.Throws<InvalidOperationException>(() => AuditSigner.SignString("canonical", weak));
        Assert.Throws<InvalidOperationException>(() => new FixedKeyProvider("k", weak));
        Assert.Throws<InvalidOperationException>(() => TraceBundleBuilder.From(result, null, weak));
    }

    [Fact]
    public void A_weak_rotation_key_is_rejected_at_construction()
        => Assert.Throws<InvalidOperationException>(() => new RotatingAuditKeyProvider("k", new byte[] { 1, 2, 3 }));

    [Fact]
    public void A_weak_PRIOR_rotation_key_is_also_rejected()
    {
        // A sub-128-bit prior key must not slip in via rotation config — prior keys still verify audits.
        var ex = Assert.Throws<InvalidOperationException>(() => new RotatingAuditKeyProvider(
            "k-current", Key(0xA1),
            new Dictionary<string, byte[]> { ["k-old"] = new byte[] { 1, 2, 3 } }));
        Assert.Contains("k-old", ex.Message);
    }

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
    public void A_bundle_signed_with_a_real_key_verifies_and_replays_under_that_key()
    {
        // The bundle's KeyId is now part of the signed canonical, so it always matches the signing key.
        var realKey = Key(0xC3);
        var provider = new RotatingAuditKeyProvider("k-real", realKey);
        var bundle = TraceBundleBuilder.From(Runtime().Run(Prompt, Workspace.CreateDemo()), null, provider);
        Assert.Equal("k-real", bundle.KeyId);
        Assert.Equal("k-real", bundle.SignedAudit.KeyId);

        Assert.True(TraceBundleBuilder.VerifySignature(bundle, provider));
        var replay = RunReplay.Reproduce(Runtime(), Workspace.CreateDemo(), bundle, provider);
        Assert.True(replay.SignatureVerified);
        Assert.True(replay.Reproduced);

        // A provider that does NOT hold the key fails closed — no silent demo-key pass.
        Assert.False(TraceBundleBuilder.VerifySignature(bundle, new RotatingAuditKeyProvider("k-other", Key(0xD4))));
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
