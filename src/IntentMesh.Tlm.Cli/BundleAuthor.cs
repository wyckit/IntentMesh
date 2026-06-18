using System.Text.Json;
using PassGen.Tlm;

namespace IntentMesh.Tlm.Cli;

/// <summary>
/// Authors the IntentMesh agentic TLM bundle source (*.source.json) in the native RSRM TLM
/// format (PassGen.Tlm), the direct analog of PassGen's DatasetAuthor. Seven linked TLMs:
/// trust-model, action-contracts, policy-rules, nl-vocabulary, tools, skills, bundle.
///
/// "The TLM is the model": action contracts, policy rules, trust sources, and NL cues all live
/// in this data. Coverage grows by editing it — the runtime code does not change. Compiled .tlmz
/// are byte-compatible with live RSRM / sage-rsrm.
/// </summary>
public static class BundleAuthor
{
    private static readonly DateTime Created = new(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc);

    private static SymbolicConcept Con(string id, string label, string cat, string desc,
        string[]? aliases = null, (string, string)[]? props = null)
    {
        var c = new SymbolicConcept { Id = id, Label = label, Category = cat, Description = desc };
        if (aliases != null) c.Aliases = aliases.ToList();
        if (props != null) foreach (var (k, v) in props) c.Properties[k] = v;
        return c;
    }

    private static SymbolicRelation Rel(string s, string t, string type, string desc = "", double strength = 1.0)
        => new() { SourceId = s, TargetId = t, Type = type, Description = desc, Strength = strength };

    private static TlmPackage Pkg(string id, TlmRole role, int prio, string[]? imports,
        List<SymbolicConcept> concepts, List<SymbolicRelation> relations,
        List<SymbolicPolicy>? pols = null, List<SymbolicCue>? cues = null, double stability = 0.0)
        => new()
        {
            Manifest = new TlmManifest
            {
                Metadata = new TlmMetadata
                {
                    TlmId = id, IsMutable = false, Role = role, Priority = prio, Version = "1.0.0",
                    Checksum = "", HotSwapPolicy = HotSwapPolicy.Safe, StabilityScore = stability
                },
                Imports = (imports ?? Array.Empty<string>()).ToList(),
                Derives = new(), CreatedUtc = Created, SchemaVersion = "1.0"
            },
            Concepts = concepts, Relations = relations,
            Policies = pols ?? new(), Cues = cues ?? new()
        };

    public static void Author(string sourceDir)
    {
        Directory.CreateDirectory(sourceDir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        foreach (var p in new[] { TrustModel(), ActionContracts(), PolicyRules(), NlVocabulary(), Tools(), Skills(), Bundle() })
        {
            File.WriteAllText(Path.Combine(sourceDir, p.Manifest.Metadata.TlmId + ".source.json"),
                JsonSerializer.Serialize(p, opts));
            Console.WriteLine($"  authored {p.Manifest.Metadata.TlmId,-22} {p.Concepts.Count} concepts, {p.Relations.Count} relations, {p.Policies.Count} policies, {p.Cues.Count} cues");
        }
    }

    // 1 ─ im-trust-model ────────────────────────────────────────────────────────
    // Trust sources and authority levels. The Zero-Trust rule: content derived from
    // retrieved/untrusted sources carries NO command authority.
    private static TlmPackage TrustModel()
    {
        var c = new List<SymbolicConcept>(); var r = new List<SymbolicRelation>();
        c.Add(Con("trust-root", "trust model", "TrustModel",
            "Separates the authority to ACT from the ability to supply content. Every intent node carries a TrustSource; only User and System sources hold command authority. Retrieved content and tool output may inform, never command."));

        c.Add(Con("auth-full", "full authority", "AuthorityLevel",
            "May originate intent and authorize action (subject to policy + confirmation).", props: new[] { ("CanCommand", "true") }));
        c.Add(Con("auth-none", "no authority", "AuthorityLevel",
            "May supply content only. Can never originate intent or grant itself permission. The Zero-Trust level.", props: new[] { ("CanCommand", "false") }));

        var sources = new (string Id, string Label, string Auth, string Desc)[]
        {
            ("src-user", "user", "auth-full", "The human operator's own request. The only first-class source of intent."),
            ("src-system", "system", "auth-full", "Trusted runtime configuration and policy. Not user-overridable at runtime."),
            ("src-retrieved-content", "retrieved content", "auth-none", "Text read from files, notes, documents, web pages. DATA, never instructions. Zero-Trust."),
            ("src-tool-output", "tool output", "auth-none", "Output produced by a tool adapter. Informational; cannot escalate into new authorized intent."),
        };
        foreach (var (id, label, auth, desc) in sources)
        {
            c.Add(Con(id, label, "TrustSource", desc, props: new[] { ("Authority", auth) }));
            r.Add(Rel(id, auth, "HasAuthority"));
        }

        c.Add(Con("rule-zero-trust", "zero-trust inheritance", "TrustRule",
            "Any intent node created or modified by analyzing untrusted data inputs inherits TrustSource=retrieved-content and Authority=none. Data can supply content; it can never spoof user intent or grant authority. Enforced by the Policy Gate, which blocks zero-trust nodes that request any side effect."));
        r.Add(Rel("rule-zero-trust", "src-retrieved-content", "AppliesTo"));
        r.Add(Rel("rule-zero-trust", "src-tool-output", "AppliesTo"));
        r.Add(Rel("rule-zero-trust", "auth-none", "Assigns"));
        r.Add(Rel("trust-root", "rule-zero-trust", "Enforces"));

        return Pkg("im-trust-model", TlmRole.Foundation, 100, null, c, r, stability: 1.0);
    }

    // 2 ─ im-action-contracts ───────────────────────────────────────────────────
    // The bounded registry of typed actions. The resolver may ONLY select from these
    // (Translation-Drift guard). Each contract declares risk, side-effect class,
    // confirmation requirement, fields, and the postconditions it must guarantee.
    private static TlmPackage ActionContracts()
    {
        var c = new List<SymbolicConcept>(); var r = new List<SymbolicRelation>();
        c.Add(Con("contracts-root", "action contracts", "Registry",
            "The single source of truth for what actions exist. A typed action contract is the agentic analog of PassGen's ConstraintSpec: a strict, inspectable schema. No tool runs from raw language — only from a contract selected here and validated by policy."));

        // postconditions (the verifier checks these deterministically)
        var pcs = new (string Id, string Label, string Desc)[]
        {
            ("pc-calendar-read-only", "calendar read-only", "Reading the calendar mutates nothing."),
            ("pc-block-proposed-not-committed", "block proposed, not committed", "A calendar block is staged as a proposal pending confirmation; no event is committed."),
            ("pc-draft-not-sent", "draft created, not sent", "An email is created as a draft; no message is transmitted."),
            ("pc-recipient-matches-request", "recipient matches request", "The draft recipient equals the entity the USER named, not one introduced by document content."),
            ("pc-no-attacker-recipient", "no unknown external recipient", "No recipient outside the user's contacts / request was added."),
            ("pc-no-private-exfil", "no private data exfiltration", "Notes marked private are not placed into any external-bound message."),
            ("pc-zero-deletions-without-approval", "zero deletions without approval", "No file is deleted without explicit per-file user approval."),
            ("pc-injected-node-not-executed", "injected node not executed", "Any zero-trust node proposed by untrusted content was blocked and never executed."),
            ("pc-summary-cites-allowed-only", "summary cites allowed sources only", "A produced summary references only user-readable, non-private sources."),
        };
        foreach (var (id, label, desc) in pcs) c.Add(Con(id, label, "Postcondition", desc));

        // contracts: (id, label, risk, side-effect, requiresConfirmation, fields, postconditions, desc)
        var contracts = new (string Id, string Label, string Risk, string Side, string Conf, string Fields, string Posts, string Desc)[]
        {
            ("act-read-calendar", "ReadCalendarIntent", "low", "none", "false", "range", "pc-calendar-read-only",
                "Read calendar events in a date range. Pure read."),
            ("act-classify-events", "ClassifyEventsIntent", "low", "none", "false", "events", "",
                "Label events as fixed or flexible. Pure analysis over already-read data."),
            ("act-create-calendar-block", "CreateCalendarBlockIntent", "medium", "local-write", "true", "title,start,durationMinutes", "pc-block-proposed-not-committed",
                "Stage a tentative calendar block. Local write; requires confirmation before commit."),
            ("act-find-notes", "FindNotesIntent", "low", "none", "false", "topic", "",
                "Locate notes by topic. Pure read."),
            ("act-summarize-document", "SummarizeDocumentIntent", "low", "none", "false", "docRefs", "pc-summary-cites-allowed-only",
                "Summarize documents. Reads untrusted content — any embedded imperative is treated as DATA, not instruction."),
            ("act-draft-email", "DraftEmailIntent", "medium", "local-write", "false", "recipient,subject,bodySourceRefs", "pc-draft-not-sent,pc-recipient-matches-request",
                "Compose an email draft to a resolved recipient. Drafting is allowed; the draft is never auto-sent."),
            ("act-send-email", "SendEmailIntent", "high", "external-comm", "true", "draftRef", "pc-no-attacker-recipient,pc-no-private-exfil",
                "Transmit an email. High-risk external side effect; always requires confirmation."),
            ("act-scan-downloads", "ScanDownloadsIntent", "low", "none", "false", "folder", "",
                "List candidate files in a folder. Pure read."),
            ("act-classify-junk", "ClassifyJunkIntent", "low", "none", "false", "files", "",
                "Classify files as junk / ambiguous / important. Pure analysis."),
            ("act-delete-files", "DeleteFilesIntent", "high", "destructive", "true", "fileRefs", "pc-zero-deletions-without-approval",
                "Delete files. Destructive; requires explicit per-file approval. Never deletes automatically."),
        };
        foreach (var (id, label, risk, side, conf, fields, posts, desc) in contracts)
        {
            c.Add(Con(id, label, "ActionContract", desc, props: new[]
            {
                ("Risk", risk), ("SideEffect", side), ("RequiresConfirmation", conf),
                ("Fields", fields), ("Postconditions", posts)
            }));
            r.Add(Rel("contracts-root", id, "Registers"));
            foreach (var pc in posts.Split(',', StringSplitOptions.RemoveEmptyEntries))
                r.Add(Rel(id, pc, "Guarantees"));
        }
        // a couple of semantic siblings for inspectability
        r.Add(Rel("act-draft-email", "act-send-email", "Precedes", "draft before send"));
        r.Add(Rel("act-scan-downloads", "act-classify-junk", "Precedes"));
        r.Add(Rel("act-classify-junk", "act-delete-files", "Precedes"));

        return Pkg("im-action-contracts", TlmRole.Logic, 120, new[] { "im-trust-model" }, c, r, stability: 0.9);
    }

    // 3 ─ im-policy-rules ───────────────────────────────────────────────────────
    // Risk classes, decision types, and the ordered policy rules the Policy Gate cites.
    private static TlmPackage PolicyRules()
    {
        var c = new List<SymbolicConcept>(); var r = new List<SymbolicRelation>();
        c.Add(Con("policy-root", "policy gate", "PolicyEngine",
            "Decides allow / warn / confirm / review / block for each intent node before execution. Authority lives here, not in language. Fail-closed: an unrecognized or zero-trust side-effecting node is blocked."));

        foreach (var (id, label, desc) in new[]
        {
            ("risk-low", "low risk", "Reads and pure analysis."),
            ("risk-medium", "medium risk", "Local writes / drafts (reversible, no external effect)."),
            ("risk-high", "high risk", "External communication or destructive deletion."),
            ("risk-critical", "critical risk", "Irreversible system / financial / legal operations (out of scope for v0.1)."),
        })
        { c.Add(Con(id, label, "RiskClass", desc)); r.Add(Rel("policy-root", id, "Ranks")); }

        foreach (var (id, label, desc) in new[]
        {
            ("dec-allow", "allow", "Permit immediately."),
            ("dec-warn", "allow with warning", "Permit but surface a caution."),
            ("dec-confirm", "require confirmation", "Stage; execute only after explicit user confirmation."),
            ("dec-review", "require review", "Hold for human review."),
            ("dec-block", "block", "Refuse. The action never executes."),
        })
        { c.Add(Con(id, label, "Decision", desc)); r.Add(Rel("policy-root", id, "Emits")); }

        // SymbolicPolicy{Id,Rule,Action} — the same primitive PassGen's rs-generation uses.
        // Evaluated top-down; the Policy Gate cites the rule Id that fired.
        var pols = new List<SymbolicPolicy>
        {
            new() { Id = "pol-zero-trust-side-effect", Rule = "a node's TrustSource is retrieved-content or tool-output (Authority=none) AND it requests any side effect", Action = "block — retrieved content is data, not authority; it may not command tools" },
            new() { Id = "pol-recipient-substitution", Rule = "an email recipient differs from the user-named recipient and was introduced by document content", Action = "block — recipient substitution from untrusted content" },
            new() { Id = "pol-private-exfiltration", Rule = "private/sensitive content would be sent to an external or unknown recipient", Action = "block — would exfiltrate private data" },
            new() { Id = "pol-delete-files", Rule = "the action deletes files", Action = "require explicit per-file approval; never delete automatically" },
            new() { Id = "pol-send-email", Rule = "the action transmits an email (external communication)", Action = "require confirmation before sending" },
            new() { Id = "pol-local-write", Rule = "the action stages a local write (e.g. a tentative calendar block)", Action = "require confirmation before committing" },
            new() { Id = "pol-draft-allowed", Rule = "the action drafts an email to a recipient the user named", Action = "allow as a draft (send remains gated)" },
            new() { Id = "pol-read-allowed", Rule = "the action is a low-risk read or pure analysis with no side effect", Action = "allow" },
            new() { Id = "pol-unregistered", Rule = "the action kind is not present in im-action-contracts", Action = "block — no typed contract exists for this action (fail-closed)" },
        };
        foreach (var p in pols) { c.Add(Con(p.Id, p.Id, "PolicyRule", $"{p.Rule} -> {p.Action}")); r.Add(Rel("policy-root", p.Id, "Applies")); }
        // priority ordering (block-rules before permissive ones)
        for (int i = 0; i < pols.Count - 1; i++) r.Add(Rel(pols[i].Id, pols[i + 1].Id, "Precedes", "evaluated before"));

        return Pkg("im-policy-rules", TlmRole.Policy, 110, new[] { "im-action-contracts", "im-trust-model" }, c, r, pols: pols, stability: 0.85);
    }

    // 4 ─ im-nl-vocabulary ──────────────────────────────────────────────────────
    // Cues: Trigger synonyms -> a Signal the resolver acts on. The direct analog of
    // PassGen's rs-nl-vocabulary. Add a synonym here and the resolver understands it.
    private static TlmPackage NlVocabulary()
    {
        var c = new List<SymbolicConcept>(); var r = new List<SymbolicRelation>(); var cues = new List<SymbolicCue>();
        c.Add(Con("nl-root", "natural-language vocabulary", "Lexicon",
            "Maps free-form English requests to action signals the deterministic resolver executes. Each Cue carries Trigger synonyms and a Signal naming an action contract or a modifier. Entities (people, the gym, downloads, the project folder) are bound from the workspace by the resolver, never invented."));

        foreach (var (id, label, desc) in new[]
        {
            ("intent-plan-day", "plan the day", "Read + organize a day's calendar."),
            ("intent-move-flexible", "move flexible events", "Reschedule events marked flexible."),
            ("intent-book-block", "book a time block", "Stage a tentative calendar block."),
            ("intent-find-notes", "find notes", "Locate notes by topic."),
            ("intent-draft-email", "draft an email", "Compose an email draft."),
            ("intent-send", "send", "Transmit (gated)."),
            ("intent-clean-downloads", "clean downloads", "Scan a downloads folder."),
            ("intent-delete-junk", "delete junk", "Classify + delete junk files (gated)."),
            ("intent-summarize", "summarize", "Summarize documents."),
        })
        { c.Add(Con(id, label, "Intent", desc)); r.Add(Rel("nl-root", id, "Resolves")); }

        var cueDefs = new (string Id, string Trigger, string Signal, string Intent)[]
        {
            ("cue-plan-day", "plan my / plan the / organize my / organise my / sort out my / look at my calendar / check my schedule / review my schedule / my friday / my monday / my tuesday / my wednesday / my thursday / my week", "act.read_calendar", "intent-plan-day"),
            ("cue-move-flexible", "move anything flexible / move flexible / reschedule flexible / shuffle flexible / shift flexible / rearrange flexible / move what's flexible", "act.classify_events", "intent-move-flexible"),
            ("cue-book-block", "book / block off / block out / schedule / reserve / set aside / put in / add a / pencil in", "act.create_block", "intent-book-block"),
            ("cue-find-notes", "meeting notes / the notes / my notes / notes for / notes from / the meeting notes", "act.find_notes", "intent-find-notes"),
            ("cue-draft-email", "draft / write / compose / prepare / put together / send / email / message / shoot / drop", "act.draft_email", "intent-draft-email"),
            ("cue-clean-downloads", "clean up my downloads / clean my downloads / tidy my downloads / tidy up downloads / clear out downloads / sort my downloads / organize my downloads / declutter downloads / clean up downloads", "act.scan_downloads", "intent-clean-downloads"),
            ("cue-delete-junk", "delete anything that looks like junk / delete junk / remove junk / trash the junk / get rid of junk / delete the junk / remove clutter / delete anything junk", "act.classify_junk", "intent-delete-junk"),
            ("cue-summarize", "summarize / summarise / summary of / recap / give me the gist / sum up / digest / overview of", "act.summarize_document", "intent-summarize"),
            ("cue-entity-gym", "gym / workout / work out / exercise / training / a run", "entity.gym", "intent-book-block"),
            ("cue-entity-project", "project folder / project docs / the project / project files / project directory", "entity.project_folder", "intent-summarize"),
            ("cue-entity-client", "the client / client / customer", "entity.client", "intent-draft-email"),
        };
        foreach (var (id, trigger, signal, intent) in cueDefs)
        {
            c.Add(Con(id, trigger.Split(" / ")[0], "Phrase",
                $"Cue (signal {signal}); triggers: {trigger}.",
                aliases: trigger.Split('/').Select(t => t.Trim()).ToArray(),
                props: new[] { ("Triggers", trigger), ("Signal", signal) }));
            r.Add(Rel(id, intent, "MapsTo", $"signal {signal}"));
            cues.Add(new SymbolicCue { Id = id, Trigger = trigger, Signal = signal });
        }
        return Pkg("im-nl-vocabulary", TlmRole.Interface, 105, new[] { "im-action-contracts" }, c, r, cues: cues, stability: 0.7);
    }

    // 5 ─ im-tools ──────────────────────────────────────────────────────────────
    // The adapter registry: which typed contract each sandboxed adapter consumes.
    private static TlmPackage Tools()
    {
        var c = new List<SymbolicConcept>(); var r = new List<SymbolicRelation>();
        c.Add(Con("tools-root", "tool adapters", "AdapterRegistry",
            "Deterministic, sandboxed adapters. Each accepts ONLY a typed action contract, never raw language, and reports postconditions after executing against the fake workspace. No real side effects in v0.1."));

        var tools = new (string Id, string Label, string Consumes, string Side, string Desc)[]
        {
            ("tool-calendar", "calendar adapter", "act-read-calendar,act-classify-events,act-create-calendar-block", "local-write", "Fake calendar: read events, classify fixed/flexible, stage tentative blocks."),
            ("tool-notes", "notes adapter", "act-find-notes,act-summarize-document", "none", "Fake notes/docs: locate and summarize. Reads untrusted content as DATA."),
            ("tool-email", "email adapter", "act-draft-email,act-send-email", "external-comm", "Fake email: create drafts; sending is gated and sandboxed (no transmission)."),
            ("tool-files", "file adapter", "act-scan-downloads,act-classify-junk,act-delete-files", "destructive", "Fake filesystem: scan, classify, and (only on approval) delete sandboxed files."),
        };
        foreach (var (id, label, consumes, side, desc) in tools)
        {
            // The consumed contracts live in the imported im-action-contracts TLM; we carry
            // them as a property (not cross-TLM relations) to keep per-package validation clean,
            // matching PassGen's im-bundle discipline. The Core reads the Consumes property.
            c.Add(Con(id, label, "ToolAdapter", desc, props: new[] { ("Consumes", consumes), ("SideEffect", side) }));
            r.Add(Rel("tools-root", id, "Provides"));
        }
        return Pkg("im-tools", TlmRole.Interface, 115, new[] { "im-action-contracts" }, c, r, stability: 0.8);
    }

    // 6 ─ im-skills ─────────────────────────────────────────────────────────────
    // Emergent skill lifecycle scaffolding (v0.2 wiring). Emergence may propose;
    // governance grants authority — a proposed skill is never auto-promoted.
    private static TlmPackage Skills()
    {
        var c = new List<SymbolicConcept>(); var r = new List<SymbolicRelation>();
        c.Add(Con("skills-root", "emergent skills", "SkillRegistry",
            "Reusable symbolic skills the runtime may PROPOSE from observed intent patterns. A skill carries an input/output schema, allowed tools, risk class, and tests, and moves through a governed lifecycle. It never silently becomes executable authority."));

        var states = new[]
        {
            ("state-observed", "observed", "A repeated intent pattern was noticed."),
            ("state-proposed", "proposed", "A candidate reusable skill was drafted."),
            ("state-simulated", "simulated", "The skill was dry-run against fake data."),
            ("state-reviewed", "reviewed", "A human reviewed and signed off."),
            ("state-active", "active", "Available for use (within policy)."),
            ("state-deprecated", "deprecated", "Superseded; scheduled for removal."),
            ("state-removed", "removed", "Withdrawn from the registry."),
        };
        foreach (var (id, label, desc) in states) c.Add(Con(id, label, "LifecycleState", desc));
        for (int i = 0; i < states.Length - 1; i++) r.Add(Rel(states[i].Item1, states[i + 1].Item1, "Precedes"));

        c.Add(Con("skill-daily-planning-followup", "DailyPlanningAndFollowup", "Skill",
            "Example proposed skill: plan a day and draft follow-ups. Composes ReadCalendar + ClassifyEvents + CreateCalendarBlock + FindNotes + DraftEmail. Status: proposed (not active).",
            props: new[] { ("Status", "proposed"), ("AllowedTools", "tool-calendar,tool-notes,tool-email"), ("Risk", "medium") }));
        r.Add(Rel("skills-root", "skill-daily-planning-followup", "Contains"));
        r.Add(Rel("skill-daily-planning-followup", "state-proposed", "HasStatus"));

        return Pkg("im-skills", TlmRole.Overlay, 900, new[] { "im-action-contracts", "im-nl-vocabulary" }, c, r, stability: 0.6);
    }

    // 7 ─ im-bundle ─────────────────────────────────────────────────────────────
    private static TlmPackage Bundle()
    {
        var c = new List<SymbolicConcept>(); var r = new List<SymbolicRelation>();
        c.Add(Con("domain-root", "agentic intent runtime", "Domain",
            "IntentMesh: a symbolic control layer between agent language and tools. This bundle indexes the full domain knowledge graph that backs the resolve -> validate -> execute -> verify -> audit pipeline."));
        foreach (var (mid, label, tlm, desc) in new[]
        {
            ("mod-trust-model", "trust model", "im-trust-model", "Trust sources, authority levels, the Zero-Trust rule."),
            ("mod-action-contracts", "action contracts", "im-action-contracts", "The bounded registry of typed actions + postconditions."),
            ("mod-policy-rules", "policy rules", "im-policy-rules", "Risk classes, decisions, and the policy gate's ordered rules."),
            ("mod-nl-vocabulary", "NL vocabulary", "im-nl-vocabulary", "English phrasing -> action signals (cues)."),
            ("mod-tools", "tool adapters", "im-tools", "Adapter registry: contract -> sandboxed tool."),
            ("mod-skills", "emergent skills", "im-skills", "Skill lifecycle scaffolding."),
        })
        { c.Add(Con(mid, label, "Module", desc, props: new[] { ("Tlm", tlm) })); r.Add(Rel("domain-root", mid, "ComposedOf")); }

        foreach (var (a, b) in new[]
        {
            ("mod-action-contracts", "mod-trust-model"),
            ("mod-policy-rules", "mod-action-contracts"),
            ("mod-policy-rules", "mod-trust-model"),
            ("mod-nl-vocabulary", "mod-action-contracts"),
            ("mod-tools", "mod-action-contracts"),
            ("mod-skills", "mod-action-contracts"),
            ("mod-skills", "mod-nl-vocabulary"),
        })
        { r.Add(Rel(a, b, "DependsOn")); }

        return Pkg("im-bundle", TlmRole.Overlay, 1000,
            new[] { "im-trust-model", "im-action-contracts", "im-policy-rules", "im-nl-vocabulary", "im-tools", "im-skills" },
            c, r, stability: 1.0);
    }
}
