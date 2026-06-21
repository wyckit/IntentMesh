using System.Text;
using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Unit-tests the multi-tenant auth primitives: session tokens are HMAC-bound and reject tamper /
/// expiry / wrong-key / type-confusion; the principal store authenticates by API-key hash in constant
/// time; and server-issued approval challenges are bound to a specific run + tenant (a challenge for
/// one run can never approve another). These are the building blocks the Control Room enforces.
/// </summary>
public sealed class AuthTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("intentmesh-auth-key-0123456789ab");   // >=16 bytes
    private static readonly byte[] OtherKey = Encoding.UTF8.GetBytes("a-totally-different-auth-key-9999");
    private const long Now = 1_000_000;

    private static AuthPrincipal Alice() =>
        new("alice", "acme", Roles.Set(new[] { Roles.Operator, Roles.Approver }));

    [Fact]
    public void Session_token_round_trips_principal_tenant_and_roles()
    {
        var svc = new AuthTokenService(Key);
        var token = svc.Mint(Alice(), Now, Now + 3600, "nonce-1");

        Assert.True(svc.TryVerify(token, Now + 10, out var p));
        Assert.Equal("alice", p.PrincipalId);
        Assert.Equal("acme", p.TenantId);
        Assert.True(p.Has(Roles.Operator));
        Assert.True(p.Has(Roles.Approver));
        Assert.False(p.Has(Roles.Viewer));   // not granted, and not admin
    }

    [Fact]
    public void Admin_role_is_a_superset()
    {
        var admin = new AuthPrincipal("root", "acme", Roles.Set(new[] { Roles.Admin }));
        Assert.True(admin.Has(Roles.Viewer));
        Assert.True(admin.Has(Roles.Operator));
        Assert.True(admin.Has(Roles.Approver));
    }

    [Fact]
    public void A_tampered_session_token_does_not_verify()
    {
        var svc = new AuthTokenService(Key);
        var token = svc.Mint(Alice(), Now, Now + 3600, "n");
        // Flip a character in the payload segment.
        var parts = token.Split('.');
        parts[1] = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        Assert.False(svc.TryVerify(string.Join('.', parts), Now + 10, out _));
    }

    [Fact]
    public void An_expired_session_token_does_not_verify()
    {
        var svc = new AuthTokenService(Key);
        var token = svc.Mint(Alice(), Now, Now + 60, "n");
        Assert.False(svc.TryVerify(token, Now + 61, out _));
    }

    [Fact]
    public void A_session_token_signed_with_another_key_does_not_verify()
    {
        var token = new AuthTokenService(Key).Mint(Alice(), Now, Now + 3600, "n");
        Assert.False(new AuthTokenService(OtherKey).TryVerify(token, Now + 10, out _));
    }

    [Fact]
    public void An_approval_challenge_cannot_be_replayed_as_a_session_token()
    {
        var challenge = new ApprovalChallengeService(Key).Mint("run1", "node-3", "acme", Now, Now + 300, "n");
        // Same key, same codec — but the typ field is "appr", not "sess".
        Assert.False(new AuthTokenService(Key).TryVerify(challenge, Now + 10, out _));
    }

    [Fact]
    public void A_session_token_cannot_be_used_as_an_approval_challenge()
    {
        var session = new AuthTokenService(Key).Mint(Alice(), Now, Now + 3600, "n");
        Assert.False(new ApprovalChallengeService(Key).TryVerify(session, "run1", "acme", Now + 10, out _));
    }

    [Fact]
    public void Principal_store_authenticates_a_correct_api_key_and_rejects_a_wrong_one()
    {
        const string apiKey = "sk-alice-supersecret";
        var store = new PrincipalStore(new[]
        {
            new PrincipalRecord("alice", "acme", new[] { Roles.Operator }, PrincipalStore.HashApiKey(apiKey)),
        });

        Assert.True(store.TryAuthenticate(apiKey, out var p));
        Assert.Equal("alice", p.PrincipalId);
        Assert.Equal("acme", p.TenantId);
        Assert.True(p.Has(Roles.Operator));

        Assert.False(store.TryAuthenticate("sk-wrong", out _));
        Assert.False(store.TryAuthenticate("", out _));
    }

    [Fact]
    public void Principal_store_loads_from_json()
    {
        var json = $$"""
        [{"id":"bob","tenant":"globex","roles":["viewer"],"apiKeyHash":"{{PrincipalStore.HashApiKey("sk-bob")}}"}]
        """;
        var store = PrincipalStore.FromJson(json);
        Assert.Equal(1, store.Count);
        Assert.True(store.TryAuthenticate("sk-bob", out var p));
        Assert.Equal("globex", p.TenantId);
    }

    [Fact]
    public void Approval_challenge_verifies_only_for_its_own_run_and_tenant()
    {
        var svc = new ApprovalChallengeService(Key);
        var challenge = svc.Mint("runA", "node-2", "acme", Now, Now + 300, "n");

        Assert.True(svc.TryVerify(challenge, "runA", "acme", Now + 10, out var node));
        Assert.Equal("node-2", node);

        Assert.False(svc.TryVerify(challenge, "runB", "acme", Now + 10, out _));    // different run
        Assert.False(svc.TryVerify(challenge, "runA", "globex", Now + 10, out _));  // different tenant
        Assert.False(svc.TryVerify(challenge, "runA", "acme", Now + 301, out _));   // expired
    }

    [Fact]
    public void Trusted_proxy_headers_map_to_a_principal_and_reject_invalid_ids()
    {
        Assert.True(TrustedProxyAuth.TryFromHeaders("alice", "acme", "operator, approver", out var p));
        Assert.Equal("acme", p.TenantId);
        Assert.True(p.Has(Roles.Approver));

        Assert.False(TrustedProxyAuth.TryFromHeaders("alice/../etc", "acme", "viewer", out _));  // traversal id
        Assert.False(TrustedProxyAuth.TryFromHeaders("alice", "", "viewer", out _));             // empty tenant
    }
}
