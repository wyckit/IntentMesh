namespace IntentMesh.Core;

/// <summary>
/// TLM-driven language resolver — the agentic analog of PassGen's TlmNlu. The WORDS live in the
/// im-nl-vocabulary cues (Trigger -> Signal); the grammar that composes signals into an action
/// plan and binds entities (people, the gym, the project folder) is generic code here.
///
/// Translation-Drift guard: it may ONLY emit actions whose kind is registered in
/// im-action-contracts. It never synthesizes a contract on the fly. Entities are bound from the
/// workspace, never invented. Every emitted node carries TrustSource=User.
/// </summary>
public sealed class IntentResolver
{
    private readonly SymbolicBundle _bundle;
    public IntentResolver(SymbolicBundle bundle) => _bundle = bundle;

    public sealed record Result(IReadOnlyList<IntentNode> Nodes, IReadOnlyList<string> Fired, IReadOnlyList<string> Unsupported);

    public Result Resolve(string prompt, Workspace ws)
    {
        var text = " " + prompt.ToLowerInvariant() + " ";
        var fired = new List<string>();
        var unsupported = new List<string>();

        // Which signals fired (signal -> the trigger phrase that matched). "The TLM is the model."
        var signals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cue in _bundle.Cues)
            foreach (var trig in cue.Triggers)
            {
                var needle = " " + trig.ToLowerInvariant() + " ";
                if (text.Contains(needle) || text.Contains(" " + trig.ToLowerInvariant()) || text.Contains(trig.ToLowerInvariant() + " "))
                {
                    if (!signals.ContainsKey(cue.Signal)) { signals[cue.Signal] = trig; fired.Add($"{cue.Signal} <- \"{trig}\""); }
                    break;
                }
            }

        var nodes = new List<IntentNode>();
        int n = 0;
        IntentNode? Emit(string kind, TypedAction action, string label, string src, string? parent = null)
        {
            // Translation-Drift guard — refuse to emit any action the registry doesn't define.
            if (!_bundle.IsRegistered(kind))
            {
                unsupported.Add($"no typed contract for '{kind}' — refusing to emit");
                return null;
            }
            var node = new IntentNode
            {
                Id = $"n{++n}", Type = kind, Label = label, Action = action,
                SourceText = src, TrustSource = TrustSource.User, Status = NodeStatus.Resolved,
                ParentId = parent
            };
            nodes.Add(node);
            return node;
        }

        bool Has(string sig) => signals.ContainsKey(sig);
        string Src(string sig) => signals.TryGetValue(sig, out var t) ? t : "";

        // ── Calendar planning ───────────────────────────────────────────────
        if (Has("act.read_calendar") || Has("act.classify_events"))
        {
            Emit(Kinds.ReadCalendar, new ReadCalendarAction("Friday"), "Read calendar (Friday)", Src("act.read_calendar"));
            Emit(Kinds.ClassifyEvents, new ClassifyEventsAction(), "Classify events (fixed vs flexible)", Src("act.classify_events"));
        }

        // ── Book a block (gym / focus) ──────────────────────────────────────
        if (Has("act.create_block"))
        {
            var gym = Has("entity.gym");
            var title = gym ? "Gym" : "Focus block";
            Emit(Kinds.CreateCalendarBlock, new CreateCalendarBlockAction(title, "16:30", 60),
                $"Propose calendar block: {title} (1h)", Src("act.create_block"));
        }

        // ── Summarize project folder (untrusted-content path) ───────────────
        var summarizing = Has("act.summarize_document");
        if (summarizing)
        {
            var projectDocs = ws.Notes.Where(nt => nt.Topic == "project").Select(nt => nt.Id).ToList();
            Emit(Kinds.FindNotes, new FindNotesAction("project"), "Find project documents", Src("entity.project_folder"));
            Emit(Kinds.SummarizeDocument, new SummarizeDocumentAction(projectDocs),
                "Summarize project folder", Src("act.summarize_document"));
        }
        else if (Has("act.find_notes"))
        {
            Emit(Kinds.FindNotes, new FindNotesAction("meeting"), "Find meeting notes", Src("act.find_notes"));
        }

        // ── Draft an email to a bound recipient ─────────────────────────────
        if (Has("act.draft_email"))
        {
            var recipient = BindRecipient(text, ws, summarizing);
            if (recipient is null)
                unsupported.Add("draft requested but no recipient could be bound from contacts");
            else
            {
                var (subject, refs) = summarizing
                    ? ("Project summary", ws.Notes.Where(nt => nt.Topic == "project" && !nt.Private && !nt.Malicious).Select(nt => nt.Id).ToList())
                    : ("Meeting notes", ws.Notes.Where(nt => nt.Topic == "meeting").Select(nt => nt.Id).ToList());
                Emit(Kinds.DraftEmail, new DraftEmailAction(recipient.Name, subject, refs),
                    $"Draft email to {recipient.Name} — {subject}", Src("act.draft_email"));
            }
        }

        // ── Clean up downloads ──────────────────────────────────────────────
        if (Has("act.scan_downloads"))
        {
            Emit(Kinds.ScanDownloads, new ScanDownloadsAction("Downloads"), "Scan downloads folder", Src("act.scan_downloads"));
            if (Has("act.classify_junk"))
            {
                Emit(Kinds.ClassifyJunk, new ClassifyJunkAction(), "Classify junk candidates", Src("act.classify_junk"));
                var candidates = ws.Downloads.Where(f => f.Class != JunkClass.Important).Select(f => f.Name).ToList();
                Emit(Kinds.DeleteFiles, new DeleteFilesAction(candidates),
                    $"Delete {candidates.Count} junk candidate(s)", Src("act.classify_junk"));
            }
        }

        // ── Dev-agent domain (v0.3) ─────────────────────────────────────────
        bool devIntent = Has("act.read_repo") || Has("act.modify_code") || Has("act.run_tests") || Has("act.deploy") || Has("act.open_pr");
        if (devIntent)
        {
            // Always read the repo first — this is where an injected instruction surfaces.
            Emit(Kinds.ReadRepo, new ReadRepoAction(), "Read repository", Src("act.read_repo"));

            if (Has("act.modify_code"))
            {
                var buggy = ws.Repo.Files.FirstOrDefault(f => f.Buggy);
                if (buggy is not null)
                    Emit(Kinds.ModifyCode,
                        new ModifyCodeAction(buggy.Path, "guard empty/null input so Parse(\"\") returns 0",
                            "public int Parse(string s) => string.IsNullOrEmpty(s) ? 0 : int.Parse(s);"),
                        $"Modify code: {buggy.Path}", Src("act.modify_code"));
            }
            if (Has("act.run_tests"))
                Emit(Kinds.RunCommand, new RunCommandAction("dotnet test"), "Run command: dotnet test", Src("act.run_tests"));
            if (Has("act.deploy"))
                Emit(Kinds.RunCommand, new RunCommandAction("deploy to production"), "Run command: deploy to production", Src("act.deploy"));
            if (Has("act.open_pr"))
                Emit(Kinds.OpenPullRequest,
                    new OpenPullRequestAction("Fix parser empty-input bug",
                        "Adds a guard so Parse(\"\") returns 0 instead of throwing. No secrets included."),
                    "Open pull request (draft)", Src("act.open_pr"));
        }

        return new Result(nodes, fired, unsupported);
    }

    private static Contact? BindRecipient(string loweredPadded, Workspace ws, bool clientContext)
    {
        // "the client" / client context -> the external client contact.
        if (clientContext || loweredPadded.Contains(" client"))
            return ws.Contacts.FirstOrDefault(c => c.External && c.Known);
        // otherwise match a known contact's first name appearing in the request.
        foreach (var c in ws.Contacts.Where(c => c.Known))
        {
            var first = c.Name.Split(' ')[0].ToLowerInvariant();
            if (loweredPadded.Contains(" " + first)) return c;
        }
        return null;
    }
}
