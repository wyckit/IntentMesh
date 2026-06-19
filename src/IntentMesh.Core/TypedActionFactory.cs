using System.Text.Json;

namespace IntentMesh.Core;

/// <summary>
/// Builds a concrete <see cref="TypedAction"/> from a registered action kind + a string-keyed field
/// bag. This is the deterministic bridge a proposer (LLM or otherwise) uses to turn proposed
/// "kind + fields" into a typed contract instance. An unknown kind returns <c>null</c> — the
/// Translation-Drift boundary: the proposer can only realize kinds the registry defines.
/// </summary>
public static class TypedActionFactory
{
    public static TypedAction? Build(string kind, IReadOnlyDictionary<string, string> fields)
    {
        string S(string k, string fallback = "") => fields.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
        int I(string k, int fallback) => int.TryParse(S(k), out var n) ? n : fallback;
        IReadOnlyList<string> L(string k) => ParseList(S(k));

        return kind switch
        {
            Kinds.ReadCalendar       => new ReadCalendarAction(S("range", "Friday")),
            Kinds.ClassifyEvents     => new ClassifyEventsAction(),
            Kinds.CreateCalendarBlock=> new CreateCalendarBlockAction(S("title", "Block"), S("start", "16:30"), I("durationMinutes", 60)),
            Kinds.FindNotes          => new FindNotesAction(S("topic", "meeting")),
            Kinds.SummarizeDocument  => new SummarizeDocumentAction(L("docRefs")),
            Kinds.DraftEmail         => new DraftEmailAction(S("recipient"), S("subject"), L("bodySourceRefs")),
            Kinds.SendEmail          => new SendEmailAction(S("draftRef"), S("recipient"), L("bodySourceRefs")),
            Kinds.ScanDownloads      => new ScanDownloadsAction(S("folder", "Downloads")),
            Kinds.ClassifyJunk       => new ClassifyJunkAction(),
            Kinds.DeleteFiles        => new DeleteFilesAction(L("fileRefs")),
            Kinds.ReadRepo           => new ReadRepoAction(),
            Kinds.ModifyCode         => new ModifyCodeAction(S("path"), S("summary"), S("newContent")),
            Kinds.RunCommand         => new RunCommandAction(S("command")),
            Kinds.OpenPullRequest    => new OpenPullRequestAction(S("title"), S("body")),
            Kinds.BuildQueryPlan     => new BuildQueryPlanAction(S("operation"), S("table"), S("summary"), I("rowLimit", 0)),
            Kinds.RunQuery           => new RunQueryAction(S("table"), S("summary")),
            Kinds.FsRead             => new FsReadAction(S("path", ".")),
            Kinds.FsWrite            => new FsWriteAction(S("path"), S("content")),
            _                        => null,
        };
    }

    /// <summary>
    /// The fields a kind must carry for the action to be well-formed and unambiguous — the ones that
    /// determine its target or safety (a recipient, a path, a command, a table). A proposer must
    /// reject a proposal missing any of these rather than synthesize a default, so a malformed LLM
    /// action (send with no recipient, fs-write with no path) never becomes an executable intent.
    /// Benign read/default-bearing kinds have none.
    /// </summary>
    public static IReadOnlyList<string> RequiredFields(string kind) => kind switch
    {
        Kinds.SendEmail        => new[] { "recipient" },
        Kinds.DraftEmail       => new[] { "recipient" },
        Kinds.FsRead           => new[] { "path" },
        Kinds.FsWrite          => new[] { "path" },
        Kinds.DeleteFiles      => new[] { "fileRefs" },
        Kinds.RunCommand       => new[] { "command" },
        Kinds.ModifyCode       => new[] { "path" },
        Kinds.OpenPullRequest  => new[] { "title" },
        Kinds.BuildQueryPlan   => new[] { "operation", "table" },
        Kinds.RunQuery         => new[] { "table" },
        _                      => Array.Empty<string>(),
    };

    /// <summary>True when <paramref name="fields"/> carries a usable value for <paramref name="key"/>
    /// — present, non-blank, and not an empty JSON collection.</summary>
    public static bool HasValue(IReadOnlyDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
           && v.Trim() is not ("[]" or "{}" or "\"\"" or "''");

    /// <summary>Parse a list-valued field that may arrive as a JSON array (<c>["a","b"]</c>) or a
    /// comma-separated string.</summary>
    private static IReadOnlyList<string> ParseList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var t = raw.TrimStart();
        if (t.StartsWith('['))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<List<string>>(raw);
                if (arr is not null) return arr;
            }
            catch { /* fall through to CSV */ }
        }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
