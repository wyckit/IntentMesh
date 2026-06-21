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

/// <summary>Context the gate reasons over: the workspace, the recipients the USER actually named,
/// the capabilities granted to this runtime, and the kind->capability map (capability scoping).</summary>
public sealed record PolicyContext(
    Workspace Workspace,
    IReadOnlySet<string> UserRequestedRecipients,
    IReadOnlySet<string> GrantedCapabilities,
    IReadOnlyDictionary<string, string> Capabilities);

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

    /// <summary>The closed set of READ-only query operations (case-insensitive). Everything else is a
    /// write and is denied under a read-only role — default-deny, not deny-list.</summary>
    private static readonly HashSet<string> QueryReadOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Select", "Read", "Count", "Aggregate", "Scan", "Get", "List", "Sum", "Avg", "Min", "Max",
    };

    public PolicyDecision Evaluate(IntentNode node, PolicyContext ctx)
    {
        var trust = node.TrustSource.ToString();

        // Fail-closed: no typed contract -> block.
        if (!_bundle.Contracts.TryGetValue(node.Type, out var contract))
            return new PolicyDecision(node.Id, Decision.Block, "unknown",
                "No typed contract exists for this action (fail-closed).",
                new[] { "pol-unregistered" }, false, trust, false, false, false);

        // Capability scoping (v1.0): the action's tool requires a capability the runtime must hold.
        if (ctx.Capabilities.TryGetValue(node.Type, out var cap) && !ctx.GrantedCapabilities.Contains(cap))
            return new PolicyDecision(node.Id, Decision.Block, contract.Risk,
                $"Capability '{cap}' is not granted to this runtime.",
                new[] { "pol-capability-not-granted" }, false, trust, false, false, false);

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
            if (node.Action is RunCommandAction zrc && !ctx.Workspace.Repo.IsAllowed(zrc.Command))
            {
                rules.Add("pol-command-not-allowlisted");
                reason += $" The command '{zrc.Command}' is not on the repo allow-list either.";
            }
            return new PolicyDecision(node.Id, Decision.Block, risk, reason, rules, false, trust, sensitive, external, destructive);
        }

        // ── Dev: shell is blocked by default (allow-list), even for the user ──
        if (node.Action is RunCommandAction rc)
        {
            if (!ctx.Workspace.Repo.IsAllowed(rc.Command))
                return new PolicyDecision(node.Id, Decision.Block, risk,
                    $"Shell is blocked by default; '{rc.Command}' is not on the repository allow-list.",
                    new[] { "pol-command-not-allowlisted" }, false, trust, sensitive, false, false);
            return new PolicyDecision(node.Id, Decision.Confirm, risk,
                $"Allow-listed command '{rc.Command}': requires confirmation before running.",
                new[] { "pol-command-allowlisted" }, true, trust, sensitive, false, false);
        }

        // ── Dev: a code edit / PR that would carry a repository secret is blocked ──
        if (ContainsSecret(node.Action, ctx.Workspace, out var secretField))
            return new PolicyDecision(node.Id, Decision.Block, risk,
                $"Would expose a repository secret in the {secretField}.",
                new[] { "pol-secret-exposure" }, false, trust, true, false, false);

        // ── Data: validate the typed query plan before anything runs (fail-closed) ──
        if (node.Action is BuildQueryPlanAction qp)
        {
            var db = ctx.Workspace.Db;
            // ALLOW-LIST, case-insensitive: only known READ operations are non-destructive; anything else
            // (Delete/Drop/Truncate/Update, a lowercase variant, or an unknown op) is treated as a write
            // and blocked under a read-only role. A deny-list would pass unknown/odd-cased write ops.
            bool qpDestructive = !QueryReadOps.Contains(qp.Operation ?? "");
            if (untrusted)
            {
                var rules = new List<string> { "pol-query-untrusted" };
                var reason = "Untrusted retrieved content may not originate a database query.";
                if (qpDestructive) { rules.Add("pol-query-readonly"); reason += $" It is also a destructive '{qp.Operation}' against a {db.Role} role."; }
                return new PolicyDecision(node.Id, Decision.Block, risk, reason, rules, false, trust, false, false, qpDestructive);
            }
            if (qpDestructive)
                return new PolicyDecision(node.Id, Decision.Block, risk,
                    $"The '{db.Role}' database role does not permit a '{qp.Operation}' operation.",
                    new[] { "pol-query-readonly" }, false, trust, false, false, true);
            if (!db.HasTable(qp.Table))
                return new PolicyDecision(node.Id, Decision.Block, risk,
                    $"Query references table '{qp.Table}', which does not exist.",
                    new[] { "pol-query-table-missing" }, false, trust, false, false, false);
            if (qp.RowLimit <= 0 || qp.RowLimit > db.RowCap)
                return new PolicyDecision(node.Id, Decision.Block, risk,
                    $"Query has no row limit or exceeds the row cap ({db.RowCap}).",
                    new[] { "pol-query-unbounded" }, false, trust, false, false, false);
            return new PolicyDecision(node.Id, Decision.Allow, risk,
                $"Read-only '{qp.Operation}' on '{qp.Table}' within the row cap.",
                new[] { "pol-read-allowed" }, false, trust, false, false, false);
        }

        // A DIRECT run-query is validated by the SAME data policy as a plan — it must not fall through
        // to generic allow. Crucially, untrusted retrieved content may not originate a query even though
        // a read is side-effect "none" (so the zero-trust side-effect rule wouldn't catch it), and the
        // table must exist.
        if (node.Action is RunQueryAction rq)
        {
            var db = ctx.Workspace.Db;
            if (untrusted)
                return new PolicyDecision(node.Id, Decision.Block, risk,
                    "Untrusted retrieved content may not originate a database query.",
                    new[] { "pol-query-untrusted" }, false, trust, false, false, false);
            if (!db.HasTable(rq.Table))
                return new PolicyDecision(node.Id, Decision.Block, risk,
                    $"Query references table '{rq.Table}', which does not exist.",
                    new[] { "pol-query-table-missing" }, false, trust, false, false, false);
            return new PolicyDecision(node.Id, Decision.Allow, risk,
                $"Read-only query on '{rq.Table}'.",
                new[] { "pol-read-allowed" }, false, trust, false, false, false);
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

    private static bool ContainsSecret(TypedAction a, Workspace ws, out string field)
    {
        field = "";
        var secrets = ws.Repo.SecretValues.ToList();
        if (secrets.Count == 0) return false;
        string? payload = a switch
        {
            ModifyCodeAction m => m.NewContent,
            OpenPullRequestAction p => p.Body,
            _ => null
        };
        if (payload is null) return false;
        if (secrets.Any(s => payload.Contains(s, StringComparison.Ordinal)))
        {
            field = a is OpenPullRequestAction ? "pull-request body" : "code edit";
            return true;
        }
        return false;
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
