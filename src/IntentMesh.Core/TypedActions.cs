namespace IntentMesh.Core;

/// <summary>The bounded set of action kinds — the canonical contract ids in im-action-contracts.
/// The resolver may only emit these (Translation-Drift guard).</summary>
public static class Kinds
{
    public const string ReadCalendar = "act-read-calendar";
    public const string ClassifyEvents = "act-classify-events";
    public const string CreateCalendarBlock = "act-create-calendar-block";
    public const string FindNotes = "act-find-notes";
    public const string SummarizeDocument = "act-summarize-document";
    public const string DraftEmail = "act-draft-email";
    public const string SendEmail = "act-send-email";
    public const string ScanDownloads = "act-scan-downloads";
    public const string ClassifyJunk = "act-classify-junk";
    public const string DeleteFiles = "act-delete-files";
    // dev-agent domain (v0.3)
    public const string ReadRepo = "act-read-repo";
    public const string ModifyCode = "act-modify-code";
    public const string RunCommand = "act-run-command";
    public const string OpenPullRequest = "act-open-pull-request";
    // data-agent domain (v0.4)
    public const string BuildQueryPlan = "act-build-query-plan";
    public const string RunQuery = "act-run-query";
    // filesystem-via-MCP domain
    public const string FsRead = "act-fs-read";
    public const string FsWrite = "act-fs-write";
}

/// <summary>
/// A typed action contract instance — the agentic analog of PassGen's ConstraintSpec. Strict,
/// inspectable, never prose. Each concrete record carries exactly the fields its kind declares.
/// </summary>
public abstract record TypedAction(string Kind)
{
    /// <summary>Ordered field name/value pairs for display + audit (never free text).</summary>
    public abstract IReadOnlyList<(string Field, string Value)> Fields { get; }
}

public sealed record ReadCalendarAction(string Range) : TypedAction(Kinds.ReadCalendar)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("range", Range) }; }

public sealed record ClassifyEventsAction() : TypedAction(Kinds.ClassifyEvents)
{ public override IReadOnlyList<(string, string)> Fields => Array.Empty<(string, string)>(); }

public sealed record CreateCalendarBlockAction(string Title, string Start, int DurationMinutes) : TypedAction(Kinds.CreateCalendarBlock)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("title", Title), ("start", Start), ("durationMinutes", DurationMinutes.ToString()) }; }

public sealed record FindNotesAction(string Topic) : TypedAction(Kinds.FindNotes)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("topic", Topic) }; }

public sealed record SummarizeDocumentAction(IReadOnlyList<string> DocRefs) : TypedAction(Kinds.SummarizeDocument)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("docRefs", string.Join(", ", DocRefs)) }; }

public sealed record DraftEmailAction(string Recipient, string Subject, IReadOnlyList<string> BodySourceRefs) : TypedAction(Kinds.DraftEmail)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("recipient", Recipient), ("subject", Subject), ("bodySourceRefs", string.Join(", ", BodySourceRefs)) }; }

public sealed record SendEmailAction(string DraftRef, string Recipient, IReadOnlyList<string> BodySourceRefs) : TypedAction(Kinds.SendEmail)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("draftRef", DraftRef), ("recipient", Recipient), ("bodySourceRefs", string.Join(", ", BodySourceRefs)) }; }

public sealed record ScanDownloadsAction(string Folder) : TypedAction(Kinds.ScanDownloads)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("folder", Folder) }; }

public sealed record ClassifyJunkAction() : TypedAction(Kinds.ClassifyJunk)
{ public override IReadOnlyList<(string, string)> Fields => Array.Empty<(string, string)>(); }

public sealed record DeleteFilesAction(IReadOnlyList<string> FileRefs) : TypedAction(Kinds.DeleteFiles)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("fileRefs", string.Join(", ", FileRefs)) }; }

// ── dev-agent domain (v0.3) ─────────────────────────────────────────────────
public sealed record ReadRepoAction() : TypedAction(Kinds.ReadRepo)
{ public override IReadOnlyList<(string, string)> Fields => Array.Empty<(string, string)>(); }

public sealed record ModifyCodeAction(string Path, string Summary, string NewContent) : TypedAction(Kinds.ModifyCode)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("path", Path), ("summary", Summary) }; }

public sealed record RunCommandAction(string Command) : TypedAction(Kinds.RunCommand)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("command", Command) }; }

public sealed record OpenPullRequestAction(string Title, string Body) : TypedAction(Kinds.OpenPullRequest)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("title", Title) }; }

// ── data-agent domain (v0.4) ────────────────────────────────────────────────
// A typed query plan (AST), not raw SQL. The validator gates it before any execution.
public sealed record BuildQueryPlanAction(string Operation, string Table, string Summary, int RowLimit) : TypedAction(Kinds.BuildQueryPlan)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("operation", Operation), ("table", Table), ("rowLimit", RowLimit.ToString()), ("summary", Summary) }; }

public sealed record RunQueryAction(string Table, string Summary) : TypedAction(Kinds.RunQuery)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("table", Table), ("summary", Summary) }; }

// ── filesystem-via-MCP domain ───────────────────────────────────────────────
public sealed record FsReadAction(string Path) : TypedAction(Kinds.FsRead)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("path", Path) }; }

public sealed record FsWriteAction(string Path, string Content) : TypedAction(Kinds.FsWrite)
{ public override IReadOnlyList<(string, string)> Fields => new[] { ("path", Path) }; }
