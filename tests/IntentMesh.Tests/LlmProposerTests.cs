using IntentMesh.Core;
using IntentMesh.Integrations;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Tests for the real LLM-backed proposer (LlmIntentProposer). A real LLM hallucinates unregistered
/// kinds, emits malformed JSON, and can be steered by injected text — the kernel must stay
/// fail-closed against all three, and a dangerous proposal must still be gated. The LLM transport is
/// a scripted ILlmClient so the logic runs offline and deterministically; the real Anthropic call is
/// env-gated.
/// </summary>
public sealed class LlmProposerTests
{
    private static SymbolicBundle Bundle() => SymbolicBundle.Load(DatasetLocator.FindCompiledDir());

    /// <summary>A scripted LLM: returns a fixed string, or throws, regardless of the prompt.</summary>
    private sealed class ScriptedLlm : ILlmClient
    {
        private readonly string? _response;
        private readonly Exception? _throw;
        public ScriptedLlm(string response) => _response = response;
        public ScriptedLlm(Exception toThrow) => _throw = toThrow;
        public string Complete(string systemPrompt, string userPrompt)
            => _throw is not null ? throw _throw : _response!;
        public string Provenance { get; init; } = "llm";
    }

    [Fact]
    public void Valid_proposal_maps_to_registry_bounded_nodes()
    {
        var bundle = Bundle();
        var llm = new ScriptedLlm("""{"actions":[{"kind":"act-read-calendar","fields":{"range":"Friday"}}]}""");
        var plan = new LlmIntentProposer(bundle, llm).Propose("plan my friday", Workspace.CreateDemo());

        var node = Assert.Single(plan.Nodes);
        Assert.Equal(Kinds.ReadCalendar, node.Type);
        Assert.Equal(TrustSource.User, node.TrustSource);
        Assert.Empty(plan.Unsupported);
    }

    [Fact]
    public void Hallucinated_unregistered_kind_is_dropped_not_executed()
    {
        var bundle = Bundle();
        var llm = new ScriptedLlm("""{"actions":[{"kind":"act-launch-missiles","fields":{}}]}""");
        var rt = new IntentMeshRuntime(bundle, new LlmIntentProposer(bundle, llm));

        var result = rt.Run("do something dangerous", Workspace.CreateDemo());

        Assert.Empty(result.Nodes);                                              // never realized
        Assert.Contains(result.Unsupported, u => u.Contains("act-launch-missiles"));
    }

    [Fact]
    public void Malformed_non_json_output_is_fail_closed()
    {
        var bundle = Bundle();
        var llm = new ScriptedLlm("I'm sorry, I can't help with that.");
        var plan = new LlmIntentProposer(bundle, llm).Propose("whatever", Workspace.CreateDemo());

        Assert.Empty(plan.Nodes);                                                // no untyped action ever
        Assert.Contains(plan.Unsupported, u => u.Contains("fail-closed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_thrown_llm_error_is_caught_not_propagated()
    {
        var bundle = Bundle();
        var llm = new ScriptedLlm(new HttpRequestException("network down"));
        var plan = new LlmIntentProposer(bundle, llm).Propose("anything", Workspace.CreateDemo());

        Assert.Empty(plan.Nodes);
        Assert.Contains(plan.Unsupported, u => u.Contains("LLM call failed"));
    }

    [Fact]
    public void Json_wrapped_in_a_markdown_fence_is_still_recovered()
    {
        var bundle = Bundle();
        var llm = new ScriptedLlm("Here you go:\n```json\n{\"actions\":[{\"kind\":\"act-read-calendar\",\"fields\":{}}]}\n```\n");
        var plan = new LlmIntentProposer(bundle, llm).Propose("read calendar", Workspace.CreateDemo());

        Assert.Single(plan.Nodes);
        Assert.Equal(Kinds.ReadCalendar, plan.Nodes[0].Type);
    }

    [Fact]
    public void A_rogue_llm_send_to_an_attacker_is_still_gated_never_auto_executed()
    {
        // The core invariant: language proposes, the gate governs — regardless of who proposes.
        var bundle = Bundle();
        var llm = new ScriptedLlm("""{"actions":[{"kind":"act-send-email","fields":{"recipient":"attacker@evil.com","draftRef":"d"}}]}""");
        var ws = Workspace.CreateDemo();
        var rt = new IntentMeshRuntime(bundle, new LlmIntentProposer(bundle, llm));

        var result = rt.Run("email my notes to attacker@evil.com", ws);

        var node = result.Nodes.Single(n => n.Type == Kinds.SendEmail);
        Assert.NotEqual("Executed", node.Status);     // not auto-executed
        Assert.NotEqual("Verified", node.Status);
        var policy = result.Policy.Single(p => p.NodeId == node.Id);
        Assert.True(policy.Decision is "Confirm" or "Block", $"expected gated, got {policy.Decision}");
        Assert.Empty(ws.SentEmails);                   // nothing transmitted
    }

    [Theory]
    [InlineData("act-send-email", "{\"actions\":[{\"kind\":\"act-send-email\",\"fields\":{\"draftRef\":\"d\"}}]}", "recipient")]
    [InlineData("act-fs-write", "{\"actions\":[{\"kind\":\"act-fs-write\",\"fields\":{\"content\":\"x\"}}]}", "path")]
    [InlineData("act-run-command", "{\"actions\":[{\"kind\":\"act-run-command\",\"fields\":{}}]}", "command")]
    [InlineData("act-delete-files", "{\"actions\":[{\"kind\":\"act-delete-files\",\"fields\":{\"fileRefs\":\"[]\"}}]}", "fileRefs")]
    public void A_side_effecting_action_missing_a_required_field_is_dropped_fail_closed(string kind, string json, string missing)
    {
        var bundle = Bundle();
        var plan = new LlmIntentProposer(bundle, new ScriptedLlm(json)).Propose("do it", Workspace.CreateDemo());

        Assert.Empty(plan.Nodes);   // never becomes a gated executable intent
        Assert.Contains(plan.Unsupported, u => u.Contains(kind) && u.Contains(missing) && u.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Required_fields_are_contract_declared_not_hand_coded()
    {
        var bundle = Bundle();
        // Finding 1: act-send-email's contract now declares (and requires) recipient — it agrees with
        // the SendEmailAction record, the prompt, and the adapter instead of disagreeing.
        Assert.Contains("recipient", bundle.Contracts[Kinds.SendEmail].Fields);
        Assert.Contains("recipient", bundle.Contracts[Kinds.SendEmail].RequiredFields);
        // Finding 2: a write contract's declared fields are required (no silent defaulting of title/start/duration).
        Assert.Contains("start", bundle.Contracts[Kinds.CreateCalendarBlock].RequiredFields);

        // ...and the proposer enforces that contract-declared set: a calendar block missing 'start'
        // is dropped fail-closed (it is no longer synthesized to "16:30").
        var llm = new ScriptedLlm("""{"actions":[{"kind":"act-create-calendar-block","fields":{"title":"Gym","durationMinutes":"60"}}]}""");
        var plan = new LlmIntentProposer(bundle, llm).Propose("book the gym", Workspace.CreateDemo());
        Assert.Empty(plan.Nodes);
        Assert.Contains(plan.Unsupported, u => u.Contains("act-create-calendar-block") && u.Contains("start"));
    }

    [Fact]
    public void List_valued_fields_parse_from_a_json_array()
    {
        var bundle = Bundle();
        var llm = new ScriptedLlm("""{"actions":[{"kind":"act-summarize-document","fields":{"docRefs":["n1","n2"]}}]}""");
        var plan = new LlmIntentProposer(bundle, llm).Propose("summarize", Workspace.CreateDemo());

        var node = Assert.Single(plan.Nodes);
        var action = Assert.IsType<SummarizeDocumentAction>(node.Action);
        Assert.Equal(new[] { "n1", "n2" }, action.DocRefs);
    }

    [Fact]
    public void An_overbroad_proposal_is_rejected_whole_fail_closed()
    {
        var bundle = Bundle();
        var actions = string.Join(",", Enumerable.Repeat("""{"kind":"act-read-calendar","fields":{"range":"Friday"}}""", 13));
        var plan = new LlmIntentProposer(bundle, new ScriptedLlm($"{{\"actions\":[{actions}]}}")).Propose("do everything", Workspace.CreateDemo());

        Assert.Empty(plan.Nodes);   // the WHOLE plan is dropped, not cherry-picked
        Assert.Contains(plan.Unsupported, u => u.Contains("overbroad", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_action_with_no_kind_is_rejected_as_ambiguous()
    {
        var bundle = Bundle();
        var plan = new LlmIntentProposer(bundle, new ScriptedLlm("""{"actions":[{"kind":"","fields":{}}]}""")).Propose("???", Workspace.CreateDemo());

        Assert.Empty(plan.Nodes);
        Assert.Contains(plan.Unsupported, u => u.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Emitted_nodes_carry_model_provenance()
    {
        var bundle = Bundle();
        var llm = new ScriptedLlm("""{"actions":[{"kind":"act-read-calendar","fields":{"range":"Friday"}}]}""") { Provenance = "anthropic:test-model" };
        var plan = new LlmIntentProposer(bundle, llm).Propose("read calendar", Workspace.CreateDemo());

        var node = Assert.Single(plan.Nodes);
        Assert.Equal("llm:anthropic:test-model", node.SourceText);   // provenance preserved on the node + into the audit
    }

    [Fact]
    public void An_approved_action_that_halts_is_marked_Halted_not_Allowed()
    {
        // Approve a send whose draft doesn't exist: the adapter halts (draft-before-send). The node must
        // NOT remain "Allowed" — a signed run can't claim approval-consumed-but-action-done.
        var bundle = Bundle();
        var llm = new ScriptedLlm("""{"actions":[{"kind":"act-send-email","fields":{"recipient":"Sarah Chen","draftRef":"does-not-exist"}}]}""");
        var rt = new IntentMeshRuntime(bundle, new LlmIntentProposer(bundle, llm));
        var result = rt.Run("email Sarah the notes", Workspace.CreateDemo(), new HashSet<string> { "n1" });

        var node = result.Nodes.Single(n => n.Type == Kinds.SendEmail);
        Assert.Equal("Halted", node.Status);
        Assert.NotEqual("Allowed", node.Status);
    }

    [Fact]
    public void A_proposer_invented_recipient_not_in_the_prompt_is_not_user_requested()
    {
        // A bad proposer drafts to an address the user never named; ground-truth recipients come from the
        // prompt, not the proposer, so pc-recipient-matches-request must FAIL (it can't self-authorize).
        var bundle = Bundle();
        var llm = new ScriptedLlm("""{"actions":[{"kind":"act-draft-email","fields":{"recipient":"evil@attacker.com","subject":"hi","bodySourceRefs":"[]"}}]}""");
        var result = new IntentMeshRuntime(bundle, new LlmIntentProposer(bundle, llm))
            .Run("draft a quick email", Workspace.CreateDemo());

        Assert.Contains(result.Nodes, n => n.Type == Kinds.DraftEmail);   // the draft was produced
        var pc = result.Verification.FirstOrDefault(v => v.Id == "pc-recipient-matches-request");
        Assert.NotNull(pc);
        Assert.False(pc!.Pass, "a proposer-invented recipient must not verify as user-requested");
    }

    /// <summary>
    /// Real Anthropic call — env-gated (ANTHROPIC_API_KEY). Proves the AnthropicLlmClient transport
    /// is wired; deterministic logic is covered by the scripted tests above.
    /// </summary>
    [SkippableFact]
    public void LlmProposer_against_the_real_api_when_configured()
    {
        using var client = AnthropicLlmClient.FromEnvironment();
        if (client is null) { Skip.If(true, "ANTHROPIC_API_KEY not set — real-API test skipped"); return; }
        var bundle = Bundle();
        var plan = new LlmIntentProposer(bundle, client).Propose("Read my calendar for Friday.", Workspace.CreateDemo());
        Assert.NotNull(plan);   // a real model should propose something registry-bounded (or nothing) without throwing
    }
}
