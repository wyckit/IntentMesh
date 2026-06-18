namespace IntentMesh.Core;

public enum Decision { Allow, Warn, Confirm, Review, Block }

/// <summary>The gate's verdict on one node — the inspectable authority record.</summary>
public sealed record PolicyDecision(
    string ActionId,
    Decision Decision,
    string Risk,
    string Reason,
    IReadOnlyList<string> TriggeredRules,
    bool RequiresConfirmation,
    string TrustSource,
    bool Sensitive,
    bool ExternalSideEffect,
    bool Destructive);

/// <summary>Context the gate reasons over: the workspace + the recipients the USER actually named.</summary>
public sealed record PolicyContext(Workspace Workspace, IReadOnlySet<string> UserRequestedRecipients);

/// <summary>
/// The Policy / Risk Gate — generalizes PassGen's SpecValidator. Fail-closed: authority lives
/// here, not in language. The decisive rule for injected content is pol-zero-trust-side-effect:
/// a node with Authority=None that requests ANY side effect is blocked, regardless of how
/// convincing its text is — we don't even have to reason about the recipient. Recipient
/// substitution and private exfiltration are cited as additional rationale when present.
/// </summary>
public sealed class PolicyGate
{
    private readonly SymbolicBundle _bundle;
    public PolicyGate(SymbolicBundle bundle) => _bundle = bundle;

    public PolicyDecision Evaluate(IntentNode node, PolicyContext ctx)
    {
        var trust = node.TrustSource.ToString();

        // Fail-closed: no typed contract -> block.
        if (!_bundle.Contracts.TryGetValue(node.Type, out var contract))
            return new PolicyDecision(node.Id, Decision.Block, "unknown",
                "No typed contract exists for this action (fail-closed).",
                new[] { "pol-unregistered" }, false, trust, false, false, false);

        bool untrusted = node.Authority == Authority.None;
        bool external = contract.SideEffect == "external-comm";
        bool destructive = contract.SideEffect == "destructive";
        bool sideEffect = contract.SideEffect != "none";
        bool sensitive = ReferencesPrivate(node.Action, ctx.Workspace);
        var risk = contract.Risk;

        // ── Block rules (untrusted content can never command a side effect) ──
        if (untrusted && sideEffect)
        {
            var rules = new List<string> { "pol-zero-trust-side-effect" };
            var reason = $"Node derived from untrusted {Humanize(node.TrustSource)} (Authority=None); " +
                         $"retrieved content is data, not authority, and may not perform a {contract.SideEffect} action.";
            if (IsEmail(node.Action, out var recipient) && !ctx.UserRequestedRecipients.Contains(recipient, StringComparer.OrdinalIgnoreCase))
            {
                rules.Add("pol-recipient-substitution");
                reason += $" Recipient '{recipient}' was introduced by document content, not the user.";
            }
            if (sensitive && external)
            {
                rules.Add("pol-private-exfiltration");
                reason += " It would exfiltrate private notes externally.";
            }
            return new PolicyDecision(node.Id, Decision.Block, risk, reason, rules, false, trust, sensitive, external, destructive);
        }

        // ── Confirmation / allow rules (trusted user intent) ────────────────
        if (destructive)
            return new PolicyDecision(node.Id, Decision.Confirm, risk,
                "Destructive deletion: requires explicit per-file approval; nothing is deleted automatically.",
                new[] { "pol-delete-files" }, true, trust, sensitive, false, true);

        if (external)
            return new PolicyDecision(node.Id, Decision.Confirm, risk,
                "External communication: sending requires user confirmation.",
                new[] { "pol-send-email" }, true, trust, sensitive, true, false);

        if (contract.RequiresConfirmation)
            return new PolicyDecision(node.Id, Decision.Confirm, risk,
                "Local write: staged as a proposal; requires confirmation before committing.",
                new[] { "pol-local-write" }, true, trust, sensitive, false, false);

        if (node.Type == Kinds.DraftEmail && IsEmail(node.Action, out var to))
            return new PolicyDecision(node.Id, Decision.Allow, risk,
                $"Drafting allowed; recipient '{to}' matches the user's request. Send remains gated.",
                new[] { "pol-draft-allowed" }, false, trust, sensitive, false, false);

        return new PolicyDecision(node.Id, Decision.Allow, risk,
            "Low-risk read / analysis with no side effect.",
            new[] { "pol-read-allowed" }, false, trust, sensitive, false, false);
    }

    private static bool IsEmail(TypedAction a, out string recipient)
    {
        switch (a)
        {
            case DraftEmailAction d: recipient = d.Recipient; return true;
            case SendEmailAction s: recipient = s.Recipient; return true;
            default: recipient = ""; return false;
        }
    }

    private static bool ReferencesPrivate(TypedAction a, Workspace ws)
    {
        IEnumerable<string> refs = a switch
        {
            DraftEmailAction d => d.BodySourceRefs,
            SummarizeDocumentAction s => s.DocRefs,
            SendEmailAction se => se.BodySourceRefs,
            _ => Array.Empty<string>()
        };
        return refs.Any(r => ws.Notes.Any(nt => nt.Id == r && nt.Private));
    }

    private static string Humanize(TrustSource s) => s switch
    {
        TrustSource.RetrievedContent => "retrieved content",
        TrustSource.ToolOutput => "tool output",
        _ => s.ToString().ToLowerInvariant()
    };
}
