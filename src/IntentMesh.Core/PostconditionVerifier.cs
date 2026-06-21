namespace IntentMesh.Core;

/// <summary>One deterministic postcondition check with its evidence.</summary>
public sealed record VerificationResult(string Id, string Expected, string Actual, bool Pass, string Evidence);

/// <summary>
/// Proves the final state matches the approved intent — the agentic analog of PassGen's
/// SpecValidator.CheckString. Validation-Paradox guard: every check is DETERMINISTIC (equality,
/// counts, status), never a semantic judgment by the planner. Only the checks relevant to what
/// actually happened are emitted, so each scenario's panel stays focused.
///
/// Proposer/Verifier separation (v1.0): this verifier does NOT receive the original user prompt. It
/// sees only the intent graph (approved typed contracts) + the workspace (tool outputs), and asks a
/// boolean question per postcondition: did the output match the contract boundary? So it catches a
/// recipient substitution (contract says X, tool sent to Y) regardless of how persuasive the prompt
/// was — see pc-recipient-contract-match.
/// </summary>
public sealed class PostconditionVerifier
{
    public IReadOnlyList<VerificationResult> Verify(IntentGraph graph, Workspace ws, IReadOnlySet<string> userRecipients)
    {
        var r = new List<VerificationResult>();
        void Add(string id, string expected, string actual, bool pass, string evidence)
            => r.Add(new VerificationResult(id, expected, actual, pass, evidence));

        // Injected / zero-trust nodes must never execute.
        var zt = graph.Nodes.Where(n => n.TrustSource != TrustSource.User).ToList();
        if (zt.Count > 0)
        {
            bool pass = zt.All(n => n.Status == NodeStatus.Blocked);
            Add("pc-injected-node-not-executed", "zero-trust nodes never execute",
                pass ? "all blocked" : "one executed!", pass,
                $"{zt.Count} zero-trust node(s); blocked={zt.Count(n => n.Status == NodeStatus.Blocked)}, executed={zt.Count(n => n.Status == NodeStatus.Executed)}");
        }

        // A side effect is legitimate only if its node was executed (i.e. allowed, or approved).
        bool sentApproved = graph.Nodes.Any(n => n.Type == Kinds.SendEmail && n.TrustSource == TrustSource.User && n.Status == NodeStatus.Executed);
        bool deleteApproved = graph.Nodes.Any(n => n.Type == Kinds.DeleteFiles && n.Status == NodeStatus.Executed);
        bool blockCommitApproved = graph.Nodes.Any(n => n.Type == Kinds.CreateCalendarBlock && n.Status == NodeStatus.Executed);

        // Email postconditions.
        if (graph.Nodes.Any(n => n.Type is Kinds.DraftEmail or Kinds.SendEmail))
        {
            bool noUnapprovedSend = ws.SentEmails.Count == 0 || sentApproved;
            Add("pc-no-unapproved-send", "no email sent without approval",
                ws.SentEmails.Count == 0 ? "none sent" : (sentApproved ? "sent after approval" : "sent WITHOUT approval!"),
                noUnapprovedSend, $"drafts={ws.Drafts.Count}, sent={ws.SentEmails.Count}, approved-send-node={sentApproved}");

            bool rmatch = ws.Drafts.All(d => userRecipients.Contains(d.Recipient, StringComparer.OrdinalIgnoreCase));
            Add("pc-recipient-matches-request", "every draft recipient was named by the user",
                rmatch ? "match" : "mismatch", rmatch,
                $"drafts -> {string.Join(", ", ws.Drafts.Select(d => d.Recipient))}; user asked -> {string.Join(", ", userRecipients)}");

            // No drafted OR sent recipient may be an unknown/external party. An approved send to a
            // known, requested recipient is fine — what's forbidden is a recipient that isn't a known
            // contact (the injected-attacker case), whether merely drafted or actually transmitted.
            // A recipient is "known" if it resolves to a known contact by NAME or EMAIL (FindContact
            // accepts either) — send paths commonly address by email (sarah@company.com), not name.
            bool IsKnownRecipient(string r) => ws.FindContact(r) is { Known: true };
            bool noAttacker = ws.Drafts.All(d => IsKnownRecipient(d.Recipient))
                              && ws.SentEmails.All(IsKnownRecipient);
            Add("pc-no-attacker-recipient", "no unknown/external recipient drafted or sent",
                noAttacker ? "none" : "present", noAttacker,
                $"drafts -> {string.Join(", ", ws.Drafts.Select(d => d.Recipient))}; sent -> {string.Join(", ", ws.SentEmails)}");

            bool noExfil = ws.Drafts.All(d => !d.SourceNoteIds.Any(rid => ws.Notes.Any(nt => nt.Id == rid && nt.Private)));
            Add("pc-no-private-exfil", "no private note left in an outbound message",
                noExfil ? "none" : "leaked", noExfil,
                $"private notes referenced by drafts: {ws.Drafts.SelectMany(d => d.SourceNoteIds).Count(rid => ws.Notes.Any(nt => nt.Id == rid && nt.Private))}");

            // Contract-boundary check (Proposer/Verifier separation): a draft's recipient must equal
            // an APPROVED DraftEmail contract recipient. If a tool sent somewhere else, that's a
            // contract-boundary violation — caught here from the contract + output alone, no prompt.
            var contractRecipients = graph.Nodes
                .Where(n => n.Type == Kinds.DraftEmail && n.Action is DraftEmailAction)
                .Select(n => ((DraftEmailAction)n.Action).Recipient)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool boundaryOk = ws.Drafts.All(d => contractRecipients.Contains(d.Recipient));
            Add("pc-recipient-contract-match", "every draft recipient equals an approved contract recipient",
                boundaryOk ? "match" : "CONTRACT-BOUNDARY VIOLATION", boundaryOk,
                $"drafts -> {string.Join(", ", ws.Drafts.Select(d => d.Recipient))}; approved contracts -> {string.Join(", ", contractRecipients)}");
        }

        // Calendar block: committed only with approval.
        if (ws.ProposedBlocks.Count > 0)
        {
            bool anyCommitted = ws.ProposedBlocks.Any(b => b.Committed);
            bool ok = !anyCommitted || blockCommitApproved;
            Add("pc-block-committed-only-with-approval", "no block committed without approval",
                !anyCommitted ? "proposal only" : (blockCommitApproved ? "committed after approval" : "committed WITHOUT approval!"),
                ok, $"proposed={ws.ProposedBlocks.Count}, committed={ws.ProposedBlocks.Count(b => b.Committed)}, approved-node={blockCommitApproved}");
        }

        // Files: deleted only with approval.
        if (graph.Nodes.Any(n => n.Type == Kinds.DeleteFiles))
        {
            bool ok = ws.DeletedFiles.Count == 0 || deleteApproved;
            Add("pc-no-unapproved-deletion", "no file deleted without approval",
                ws.DeletedFiles.Count == 0 ? "none deleted" : (deleteApproved ? "deleted after approval" : "deleted WITHOUT approval!"),
                ok, $"deleted={ws.DeletedFiles.Count}, approved-node={deleteApproved}");

            // Granular check: the set of files actually deleted must be EXACTLY a subset of the per-file
            // refs that were approved (node.ApprovedRefs) — proving the adapter deleted only approved
            // files, not merely that a delete node ran. Catches an over-deleting/buggy adapter.
            var approvedRefs = graph.Nodes
                .Where(n => n.Type == Kinds.DeleteFiles)
                .SelectMany(n => n.ApprovedRefs)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unapproved = ws.DeletedFiles.Where(f => !approvedRefs.Contains(f)).ToList();
            Add("pc-deletion-matches-approval", "every deleted file was individually approved",
                unapproved.Count == 0 ? "exact match" : "deleted UNAPPROVED file(s)!", unapproved.Count == 0,
                $"deleted -> {string.Join(", ", ws.DeletedFiles)}; approved refs -> {string.Join(", ", approvedRefs)}");
        }

        // ── Dev-agent postconditions ────────────────────────────────────────
        if (graph.Nodes.Any(n => n.Type is Kinds.ModifyCode or Kinds.OpenPullRequest))
        {
            bool noPush = ws.Repo.StagedEdits.All(e => !e.Pushed) && ws.Repo.DraftPRs.All(p => !p.Pushed);
            Add("pc-edit-and-pr-not-pushed", "edits/PRs staged or drafted, never pushed",
                noPush ? "nothing pushed" : "pushed!", noPush,
                $"staged={ws.Repo.StagedEdits.Count}, draftPRs={ws.Repo.DraftPRs.Count}, pushed={ws.Repo.PushedRefs.Count}");

            var secrets = ws.Repo.SecretValues.ToList();
            bool noSecret = !ws.Repo.DraftPRs.Any(p => secrets.Any(s => p.Body.Contains(s)))
                            && !ws.Repo.StagedEdits.Any(e => secrets.Any(s => e.Summary.Contains(s)));
            Add("pc-no-secret-in-diff", "no repository secret in any edit or PR",
                noSecret ? "none" : "leaked!", noSecret, $"secrets defined={secrets.Count}");
        }

        if (graph.Nodes.Any(n => n.Type == Kinds.RunCommand))
        {
            bool allAllowed = ws.Repo.RanCommands.All(ws.Repo.IsAllowed);
            Add("pc-shell-blocked-by-default", "only allow-listed commands ran",
                allAllowed ? (ws.Repo.RanCommands.Count == 0 ? "none ran" : "allow-listed only") : "ran a blocked command!",
                allAllowed, $"ran=[{string.Join(", ", ws.Repo.RanCommands)}], allow-list=[{string.Join(", ", ws.Repo.AllowedCommands)}]");
        }

        // ── Data-agent postconditions ───────────────────────────────────────
        if (graph.Nodes.Any(n => n.Type is Kinds.BuildQueryPlan or Kinds.RunQuery))
        {
            Add("pc-readonly-no-mutation", "no destructive query ran",
                ws.Db.Mutations.Count == 0 ? "none" : "mutated!", ws.Db.Mutations.Count == 0,
                $"mutations={ws.Db.Mutations.Count}, ran-queries={ws.Db.RanQueries.Count}, role={ws.Db.Role}");

            var sensitiveVals = ws.Db.Tables
                .SelectMany(t => t.Rows.SelectMany(row => t.Sensitive
                    .Select(s => { var i = t.Columns.ToList().IndexOf(s); return i >= 0 && i < row.Length ? row[i] : ""; })))
                .Where(v => v.Length > 0).Distinct().ToList();
            bool noExposure = ws.Drafts.All(d => !sensitiveVals.Any(v => d.Body.Contains(v)));
            Add("pc-no-sensitive-exposure", "no sensitive column in an outbound report",
                noExposure ? "none" : "exposed!", noExposure, $"sensitive values guarded={sensitiveVals.Count}");
        }

        // Summary cites allowed sources only.
        if (graph.Nodes.Any(n => n.Type == Kinds.SummarizeDocument))
        {
            bool ok = ws.Drafts.All(d => !d.SourceNoteIds.Any(rid => ws.Notes.Any(nt => nt.Id == rid && (nt.Private || nt.Malicious))));
            Add("pc-summary-cites-allowed-only", "summary references only allowed sources",
                ok ? "allowed only" : "included private/malicious", ok,
                "no private or malicious note flowed into any draft");
        }

        return r;
    }
}
