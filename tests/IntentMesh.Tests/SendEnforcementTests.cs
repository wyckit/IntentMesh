using IntentMesh.Core;
using IntentMesh.Integrations;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Send/draft enforcement: a send must be preceded by a draft and transmits to the DRAFT's recipient
/// (not an independently-specified one), and an approved send to a known, requested recipient passes
/// verification (the "no attacker recipient" check is about *who*, not *whether anything was sent*).
/// </summary>
public sealed class SendEnforcementTests
{
    private static SymbolicBundle Bundle() => SymbolicBundle.Load(DatasetLocator.FindCompiledDir());

    private sealed class ScriptedLlm(string response) : ILlmClient
    {
        public string Complete(string systemPrompt, string userPrompt) => response;
    }

    private static PolicyDecision Confirm() =>
        new("n1", Decision.Confirm, "high", "external comm", new[] { "pol-send-email" },
            RequiresConfirmation: true, TrustSource: "User", Sensitive: false, ExternalSideEffect: true, Destructive: false);

    private static IntentNode SendNode(string recipient, string draftRef) => new()
    {
        Id = "s1", Type = Kinds.SendEmail, Label = "send", TrustSource = TrustSource.User, Status = NodeStatus.Resolved,
        Action = new SendEmailAction(draftRef, recipient, Array.Empty<string>()),
    };

    private static IntentNode DraftNode(string recipient) => new()
    {
        Id = "d1", Type = Kinds.DraftEmail, Label = "draft", TrustSource = TrustSource.User, Status = NodeStatus.Resolved,
        Action = new DraftEmailAction(recipient, "Notes", Array.Empty<string>()),
    };

    [Fact]
    public void An_approved_send_with_no_prior_draft_is_halted()
    {
        var ws = Workspace.CreateDemo();
        var adapter = new EmailAdapter();

        var exec = adapter.Execute(SendNode("anyone", "bogus-ref"), Confirm(), ws, approved: true);

        Assert.True(exec.Halted);            // draft-before-send enforced; a bogus draftRef can't transmit
        Assert.Empty(ws.SentEmails);
    }

    [Fact]
    public void An_approved_send_transmits_to_the_referenced_drafts_recipient()
    {
        var ws = Workspace.CreateDemo();
        var known = ws.Contacts.First(c => c.Known && !c.External).Name;
        var adapter = new EmailAdapter();

        adapter.Execute(DraftNode(known), Confirm(), ws, approved: false);                 // draft first
        var sent = adapter.Execute(SendNode(known, draftRef: known), Confirm(), ws, approved: true);

        Assert.True(sent.Ran && !sent.Halted);
        Assert.Contains(known, ws.SentEmails);
        Assert.Contains(ws.Drafts, d => d.Recipient == known && d.Sent);
    }

    [Fact]
    public void An_approved_send_to_a_known_requested_recipient_passes_no_attacker_verification()
    {
        var bundle = Bundle();
        var probe = Workspace.CreateDemo();
        var known = probe.Contacts.First(c => c.Known && !c.External).Name;
        var json = """
            {"actions":[
              {"kind":"act-draft-email","fields":{"recipient":"__R__","subject":"Notes"}},
              {"kind":"act-send-email","fields":{"draftRef":"__R__","recipient":"__R__"}}
            ]}
            """.Replace("__R__", known);
        var rt = new IntentMeshRuntime(bundle, new LlmIntentProposer(bundle, new ScriptedLlm(json)));

        var sendId = rt.Run("draft and send to " + known, Workspace.CreateDemo())
            .Nodes.First(n => n.Type == Kinds.SendEmail).Id;

        var ws = Workspace.CreateDemo();
        var run = rt.Run("draft and send to " + known, ws, new HashSet<string> { sendId });

        Assert.Contains(known, ws.SentEmails);                                  // F1: the approved send transmitted
        var noAttacker = run.Verification.First(v => v.Id == "pc-no-attacker-recipient");
        Assert.True(noAttacker.Pass, $"approved send to a known recipient must pass; evidence: {noAttacker.Evidence}");  // F2
    }
}
