using System.Text.RegularExpressions;

namespace IntentMesh.Core;

/// <summary>A node an adapter proposes from data it read. The runtime wraps it as a ZERO-TRUST node.</summary>
public sealed record ProposedNode(string Type, TypedAction Action, string Label, string SourceText, string FromSource);

/// <summary>What an adapter actually did — the evidence the verifier and audit consume.</summary>
public sealed record ExecutionResult(
    string ActionId, bool Ran, bool Halted, string Summary,
    IReadOnlyList<string> Effects, IReadOnlyList<ProposedNode> Proposed);

/// <summary>A deterministic, sandboxed tool. Accepts ONLY a typed action — never raw language —
/// and honors the policy decision (a Confirm decision performs only the safe, non-committing path).</summary>
public interface IToolAdapter
{
    bool Handles(string kind);
    ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws);
}

public sealed class ToolHost
{
    private readonly IReadOnlyList<IToolAdapter> _adapters = new IToolAdapter[]
    { new CalendarAdapter(), new NotesAdapter(), new EmailAdapter(), new FileAdapter() };

    public IToolAdapter? For(string kind) => _adapters.FirstOrDefault(a => a.Handles(kind));

    private static ExecutionResult Empty(string id, string summary) =>
        new(id, true, false, summary, Array.Empty<string>(), Array.Empty<ProposedNode>());
    public static ExecutionResult Ok(string id, string summary, params string[] effects) =>
        new(id, true, false, summary, effects, Array.Empty<ProposedNode>());
    public static ExecutionResult Halt(string id, string summary, params string[] effects) =>
        new(id, true, true, summary, effects, Array.Empty<ProposedNode>());
}

public sealed class CalendarAdapter : IToolAdapter
{
    public bool Handles(string kind) => kind is Kinds.ReadCalendar or Kinds.ClassifyEvents or Kinds.CreateCalendarBlock;

    public ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws) => node.Action switch
    {
        ReadCalendarAction => ToolHost.Ok(node.Id,
            $"Read {ws.Calendar.Count} events for Friday (no mutation).",
            $"events: {string.Join(", ", ws.Calendar.Select(e => e.Title))}"),

        ClassifyEventsAction => ToolHost.Ok(node.Id,
            "Classified events.",
            $"flexible: {string.Join(", ", ws.Calendar.Where(e => e.Flexible).Select(e => e.Title))}",
            $"fixed: {string.Join(", ", ws.Calendar.Where(e => !e.Flexible).Select(e => e.Title))}"),

        CreateCalendarBlockAction b =>
            // Confirm decision -> stage a tentative proposal; do NOT commit.
            StageBlock(node.Id, b, ws),

        _ => ToolHost.Ok(node.Id, "no-op")
    };

    private static ExecutionResult StageBlock(string id, CreateCalendarBlockAction b, Workspace ws)
    {
        ws.ProposedBlocks.Add(new CalendarBlock(b.Title, b.Start, b.DurationMinutes.ToString(), Committed: false));
        return ToolHost.Halt(id, $"Proposed '{b.Title}' at {b.Start} for {b.DurationMinutes}m (tentative).",
            "block staged as a proposal — NOT committed (awaiting confirmation)");
    }
}

public sealed class NotesAdapter : IToolAdapter
{
    public bool Handles(string kind) => kind is Kinds.FindNotes or Kinds.SummarizeDocument;

    public ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws) => node.Action switch
    {
        FindNotesAction f => ToolHost.Ok(node.Id,
            $"Found {ws.Notes.Count(nt => nt.Topic == f.Topic)} note(s) for topic '{f.Topic}'.",
            $"notes: {string.Join(", ", ws.Notes.Where(nt => nt.Topic == f.Topic).Select(nt => nt.Title))}"),

        SummarizeDocumentAction s => Summarize(node, s, ws),
        _ => ToolHost.Ok(node.Id, "no-op")
    };

    // Reads untrusted documents. Allowed content becomes a summary; an embedded imperative is
    // treated as DATA and QUARANTINED into a zero-trust proposed node (never executed as intent).
    private static ExecutionResult Summarize(IntentNode node, SummarizeDocumentAction s, Workspace ws)
    {
        var docs = s.DocRefs.Select(r => ws.Notes.First(nt => nt.Id == r)).ToList();
        var safe = docs.Where(d => !d.Malicious && !d.Private).ToList();
        var proposed = new List<ProposedNode>();
        var effects = new List<string> { $"summarized {safe.Count} allowed doc(s): {string.Join(", ", safe.Select(d => d.Title))}" };

        foreach (var mal in docs.Where(d => d.Malicious))
        {
            var addr = Regex.Match(mal.Body, @"[\w.+-]+@[\w-]+\.[\w.-]+").Value;
            var line = mal.Body.Split('.').FirstOrDefault(l => l.ToUpperInvariant().Contains("IGNORE") || l.Contains("@"))?.Trim() ?? mal.Body;
            var privateRefs = ws.Notes.Where(nt => nt.Private).Select(nt => nt.Id).ToList();
            proposed.Add(new ProposedNode(
                Kinds.SendEmail,
                new SendEmailAction("all private notes", string.IsNullOrEmpty(addr) ? "unknown@external" : addr, privateRefs),
                $"(injected) Email private notes to {addr}",
                line, mal.Title));
            effects.Add($"detected an injected instruction in '{mal.Title}' — quarantined as a zero-trust proposal, NOT executed");
        }

        return new ExecutionResult(node.Id, Ran: true, Halted: false,
            $"Summarized the project folder from {safe.Count} allowed source(s); {proposed.Count} injected instruction(s) quarantined.",
            effects, proposed);
    }
}

public sealed class EmailAdapter : IToolAdapter
{
    public bool Handles(string kind) => kind is Kinds.DraftEmail or Kinds.SendEmail;

    public ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws) => node.Action switch
    {
        DraftEmailAction d => Draft(node.Id, d, ws),
        // Sending is gated. If we ever reach here for a user node it's a Confirm -> halt (not sent).
        SendEmailAction se => ToolHost.Halt(node.Id,
            $"Send to {se.Recipient} requires confirmation — NOT sent.", "0 messages transmitted"),
        _ => ToolHost.Ok(node.Id, "no-op")
    };

    private static ExecutionResult Draft(string id, DraftEmailAction d, Workspace ws)
    {
        var contact = ws.FindContact(d.Recipient);
        var body = "Summary:\n" + string.Join("\n", d.BodySourceRefs.Select(r =>
            "- " + (ws.Notes.FirstOrDefault(nt => nt.Id == r)?.Body ?? r)));
        ws.Drafts.Add(new EmailDraft(d.Recipient, contact?.Email ?? "?", d.Subject, body, d.BodySourceRefs, Sent: false));
        return ToolHost.Ok(id, $"Created draft to {d.Recipient} ({contact?.Email}) — '{d.Subject}'.",
            "draft created, NOT sent", $"recipient = {d.Recipient}");
    }
}

public sealed class FileAdapter : IToolAdapter
{
    public bool Handles(string kind) => kind is Kinds.ScanDownloads or Kinds.ClassifyJunk or Kinds.DeleteFiles;

    public ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws) => node.Action switch
    {
        ScanDownloadsAction => ToolHost.Ok(node.Id,
            $"Scanned {ws.Downloads.Count} file(s) in Downloads.",
            $"files: {string.Join(", ", ws.Downloads.Select(f => f.Name))}"),

        ClassifyJunkAction => ToolHost.Ok(node.Id, "Classified downloads.",
            $"junk: {Names(ws, JunkClass.Junk)}", $"ambiguous: {Names(ws, JunkClass.Ambiguous)}", $"important (keep): {Names(ws, JunkClass.Important)}"),

        DeleteFilesAction del =>
            // Confirm decision -> require explicit per-file approval; delete nothing.
            ToolHost.Halt(node.Id,
                $"{del.FileRefs.Count} file(s) await explicit per-file approval — 0 deleted.",
                "0 files deleted", $"pending approval: {string.Join(", ", del.FileRefs)}"),

        _ => ToolHost.Ok(node.Id, "no-op")
    };

    private static string Names(Workspace ws, JunkClass c) =>
        string.Join(", ", ws.Downloads.Where(f => f.Class == c).Select(f => f.Name));
}
