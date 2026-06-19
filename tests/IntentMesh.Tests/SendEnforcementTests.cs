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
        var draftRef = ws.Drafts.Single().Ref;                                             // the draft's real id
        var sent = adapter.Execute(SendNode(known, draftRef), Confirm(), ws, approved: true);

        Assert.True(sent.Ran && !sent.Halted);
        Assert.Contains(known, ws.SentEmails);
        Assert.Contains(ws.Drafts, d => d.Recipient == known && d.Sent);
    }

    [Fact]
    public void A_wrong_draftRef_does_not_transmit_even_if_a_recipient_matches()
    {
        // draftRef is authoritative: an approved send to the SAME recipient but with a bogus draftRef
        // must NOT continue the existing draft (the reviewer's multiple-drafts-same-recipient concern).
        var ws = Workspace.CreateDemo();
        var known = ws.Contacts.First(c => c.Known && !c.External).Name;
        var adapter = new EmailAdapter();

        adapter.Execute(DraftNode(known), Confirm(), ws, approved: false);                 // real draft to `known`
        var sent = adapter.Execute(SendNode(known, draftRef: "bogus-id"), Confirm(), ws, approved: true);

        Assert.True(sent.Halted);
        Assert.Empty(ws.SentEmails);
    }

    [Fact]
    public void The_real_gmail_adapter_also_requires_a_prior_draft()
    {
        var transport = new NullEmailTransport();
        var adapter = OAuthAdapterWiringExample.CreateAdapter(transport);
        var ws = Workspace.CreateDemo();   // no draft

        var exec = adapter.Execute(SendNode("sarah@company.com", "bogus-ref"), Confirm(), ws, approved: true);

        Assert.True(exec.Halted);          // same draft-before-send rule as the in-memory adapter
        Assert.Empty(ws.SentEmails);
        Assert.Empty(transport.Sent);      // nothing transmitted over the (real) transport
    }

    [Fact]
    public void No_attacker_verification_recognizes_a_known_contact_by_email()
    {
        var ws = Workspace.CreateDemo();
        var known = ws.Contacts.First(c => c.Known && !c.External);

        // A draft + send addressed by the contact's EMAIL (not name) must still count as "known".
        ws.Drafts.Add(new EmailDraft("d1", known.Email, known.Email, "Notes", "body", Array.Empty<string>(), Sent: true));
        ws.SentEmails.Add(known.Email);
        var graph = new IntentGraph();
        graph.Add(new IntentNode
        {
            Id = "s1", Type = Kinds.SendEmail, Label = "send", TrustSource = TrustSource.User, Status = NodeStatus.Executed,
            Action = new SendEmailAction("ref", known.Email, Array.Empty<string>()),
        });

        var v = new PostconditionVerifier().Verify(graph, ws, new HashSet<string> { known.Email });
        var noAttacker = v.First(x => x.Id == "pc-no-attacker-recipient");
        Assert.True(noAttacker.Pass, $"a known contact addressed by email must not read as an attacker; evidence: {noAttacker.Evidence}");
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
              {"kind":"act-send-email","fields":{"draftRef":"draft-1","recipient":"__R__"}}
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
