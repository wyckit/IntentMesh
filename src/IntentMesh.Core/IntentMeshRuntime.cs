namespace IntentMesh.Core;

/// <summary>
/// The IntentMesh pipeline orchestrator. Generalizes PassGen's resolve -> validate-before-generate
/// -> generate -> check flow to many tools:
///
///   prompt -> IntentResolver -> Intent Mesh -> PolicyGate -> tool adapters -> PostconditionVerifier -> AuditTrail
///
/// Authority moves downstream, away from language. Nodes a tool proposes from untrusted content
/// are ingested as ZERO-TRUST and re-run through the SAME gate — which blocks them.
///
/// <para><b>Threading contract:</b> the runtime itself is stateless across runs (the gate, verifier,
/// and tool host hold no per-run state; <see cref="AuditSigner"/> is stateless). All per-run state
/// lives in the <see cref="Workspace"/> and the local graph/audit. A single <see cref="Workspace"/>
/// must NOT be shared across concurrent <see cref="Run(string, Workspace)"/> calls — give each run
/// its own workspace (e.g. one per web request). Distinct workspaces run concurrently safely.</para>
/// </summary>
public sealed class IntentMeshRuntime
{
    /// <summary>Default per-run blanket-approval cap. More approvals than this in a single run is
    /// treated as a blanket "approve everything" and rejected fail-closed.</summary>
    public const int DefaultMaxApprovalsPerRun = 32;

    private readonly SymbolicBundle _bundle;
    private readonly IIntentProposer _proposer;
    private readonly PolicyGate _gate;
    private readonly ToolHost _tools = new();
    private readonly PostconditionVerifier _verifier = new();
    private readonly SkillProposer _skills;
    private readonly IReadOnlySet<string> _granted;
    private readonly int _maxApprovalsPerRun;

    public SymbolicBundle Bundle => _bundle;

    /// <summary>The capabilities this runtime is granted (capability scoping). Exposed so a caller
    /// that swaps the proposer can preserve the same grants rather than silently widening them.</summary>
    public IReadOnlySet<string> GrantedCapabilities => _granted;

    /// <param name="proposer">The proposal layer (default: the rule-based IntentResolver). Swap in
    /// an LLM proposer here — nothing downstream changes.</param>
    /// <param name="grantedCapabilities">Capabilities the runtime is granted (default: all in the
    /// bundle). A node whose tool requires an ungranted capability is blocked (capability scoping).</param>
    /// <param name="maxApprovalsPerRun">Blanket-approval guard: if a single run is handed more
    /// approvals than this, they are all rejected (fail-closed) and recorded in the audit, so an
    /// "approve all pending" UI cannot rubber-stamp an unbounded batch of side effects.</param>
    public IntentMeshRuntime(SymbolicBundle bundle, IIntentProposer? proposer = null, IReadOnlySet<string>? grantedCapabilities = null,
        int maxApprovalsPerRun = DefaultMaxApprovalsPerRun)
    {
        _bundle = bundle;
        _proposer = proposer ?? new IntentResolver(bundle);
        _gate = new PolicyGate(bundle);
        _skills = new SkillProposer(bundle.Skills);
        _granted = grantedCapabilities ?? bundle.AllCapabilities;
        _maxApprovalsPerRun = maxApprovalsPerRun;
    }

    public static IntentMeshRuntime Load(string? compiledDir = null)
        => new(SymbolicBundle.LoadDefault(compiledDir));   // dataset dir if present, else the embedded bundle

    public RunResult Run(string prompt, Workspace ws) => Run(prompt, ws, new HashSet<string>());

    /// <summary>Run an externally-supplied one-shot proposer through this runtime's pipeline while
    /// PRESERVING this runtime's capability grants and approval cap. Used by the McpProxy so a proxy
    /// built on a capability-restricted runtime gates MCP calls under those same restrictions instead
    /// of defaulting back to all-capabilities-granted.</summary>
    public RunResult RunWith(IIntentProposer proposer, string prompt, Workspace ws, IReadOnlySet<string> approvals)
        => new IntentMeshRuntime(_bundle, proposer, _granted, _maxApprovalsPerRun).Run(prompt, ws, approvals);

    /// <param name="approvals">Node ids the user has explicitly approved. An approval only ever
    /// applies to a full-authority node whose decision is Confirm — it can NEVER turn a Block into
    /// execution, so a zero-trust / injected node remains blocked regardless of what is passed.</param>
    public RunResult Run(string prompt, Workspace ws, IReadOnlySet<string> approvals)
    {
        var graph = new IntentGraph();
        var audit = new AuditTrail();

        // 1. Propose: language -> typed, registry-bounded intent nodes (all TrustSource.User).
        var resolved = _proposer.Propose(prompt, ws);
        foreach (var node in resolved.Nodes)
        {
            graph.Add(node);
            audit.Add(node.Id, "resolve", $"resolved {node.Type} -> {node.Label}");
        }
        foreach (var u in resolved.Unsupported)
            audit.Add("-", "resolve", $"unsupported: {u}");

        // Recipients the USER actually requested — derived as GROUND TRUTH from the prompt + workspace
        // contacts, NOT from the proposer's own nodes. Trusting proposer output here is circular: a bad
        // (e.g. LLM) proposer could invent a recipient and make pc-recipient-matches-request believe the
        // user asked for it. A proposer-invented recipient that the prompt never names is therefore not
        // treated as user-requested, and fails the postcondition / triggers substitution rules.
        var userRecipients = DeriveUserRecipients(prompt, ws);
        var ctx = new PolicyContext(ws, userRecipients, _granted, _bundle.Capabilities);

        // Consent is part of the signed record: fold the operator's approvals into the audit chain so
        // who-approved-what is provable from the artifact alone and replaying with a different
        // approval set produces a different signature. Blanket-approval guard: too many approvals in
        // one run is rejected fail-closed.
        var effectiveApprovals = approvals;
        if (approvals.Count > _maxApprovalsPerRun)
        {
            audit.Add("-", "consent",
                $"BLANKET APPROVAL REJECTED: {approvals.Count} approvals exceed the per-run cap of {_maxApprovalsPerRun}; run treated as unapproved (fail-closed).");
            effectiveApprovals = new HashSet<string>();
        }
        else if (approvals.Count > 0)
        {
            audit.Add("-", "consent",
                $"operator approved node(s): {string.Join(", ", approvals.OrderBy(a => a, StringComparer.Ordinal))}");
        }

        // 2-4. Process the mesh; dynamically-proposed zero-trust nodes join the same pipeline.
        int counter = resolved.Nodes.Count;
        var queue = new Queue<IntentNode>(graph.Nodes);
        var seen = new HashSet<string>();
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!seen.Add(node.Id)) continue;

            var decision = _gate.Evaluate(node, ctx);
            node.Policy = decision;
            audit.Add(node.Id, "policy",
                $"{decision.Decision} ({decision.Risk}) — {decision.Reason} [rules: {string.Join(", ", decision.TriggeredRules)}]");

            if (decision.Decision == Decision.Block)
            {
                node.Status = NodeStatus.Blocked;
                node.BlockedReason = decision.Reason;
                continue;
            }

            // An approval applies ONLY to a full-authority node that the gate gated for
            // confirmation. A Block never reaches here, so injected/zero-trust nodes can't be approved.
            bool approved = false;
            if (decision.Decision == Decision.Confirm && node.Authority == Authority.Full)
            {
                if (node.Action is DeleteFilesAction del)
                {
                    // Destructive deletion requires explicit PER-FILE approval (node.Id#fileRef). A bare
                    // node-level approval does NOT blanket-approve the batch — granular consent is the
                    // whole point of per-file approval; the adapter deletes only these exact refs.
                    node.ApprovedRefs = del.FileRefs
                        .Where(r => effectiveApprovals.Contains($"{node.Id}#{r}"))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    approved = node.ApprovedRefs.Count > 0;
                }
                else
                {
                    approved = effectiveApprovals.Contains(node.Id);
                }
            }

            // Transitive-allow guard: a node proposed dynamically by an adapter (ParentId set) must
            // never auto-execute a side effect on a bare Allow — emergent side-effecting intent is
            // always staged for human-in-loop, even if the gate would otherwise allow it. (Zero-trust
            // children are already blocked upstream; this also covers any future trusted proposer.)
            bool emergentSideEffect = node.ParentId is not null
                && decision.Decision == Decision.Allow
                && _bundle.Contracts.TryGetValue(node.Type, out var ec) && ec.SideEffect != "none";
            if (emergentSideEffect)
            {
                node.Status = NodeStatus.NeedsConfirmation;
                audit.Add(node.Id, "policy",
                    "emergent side-effecting node staged for human approval (transitive-allow guard) — not auto-executed.");
                continue;
            }

            node.Status = decision.RequiresConfirmation && !approved ? NodeStatus.NeedsConfirmation : NodeStatus.Allowed;

            var adapter = _tools.For(node.Type);
            if (adapter is null)
            {
                node.Status = NodeStatus.Blocked;
                node.BlockedReason = "no tool adapter for this action kind";
                audit.Add(node.Id, "execute", "BLOCKED: no adapter");
                continue;
            }

            var exec = adapter.Execute(node, decision, ws, approved);
            node.Execution = exec;
            audit.Add(node.Id, "execute",
                (approved ? "[APPROVED] " : "") + (exec.Halted ? $"HALTED — {exec.Summary}" : exec.Summary));
            if (exec.Halted)
            {
                // An ALLOWED/approved action that still halted is a real failure — don't leave it looking
                // "Allowed" in the signed run (approval consumed but the action never happened). A node
                // merely awaiting confirmation halts by design, so keep that as NeedsConfirmation.
                if (node.Status == NodeStatus.Allowed) node.Status = NodeStatus.Halted;
            }
            else if (exec.Ran && (decision.Decision == Decision.Allow || approved))
                node.Status = NodeStatus.Executed;

            // Ingest proposed nodes as ZERO-TRUST (State-Poisoning guard) and re-queue them.
            foreach (var p in exec.Proposed)
            {
                var zt = new IntentNode
                {
                    Id = $"n{++counter}",
                    Type = p.Type,
                    Label = p.Label,
                    Action = p.Action,
                    SourceText = p.SourceText,
                    TrustSource = TrustSource.RetrievedContent,
                    Status = NodeStatus.Resolved,
                    ParentId = node.Id
                };
                graph.Add(zt);
                audit.Add(zt.Id, "resolve",
                    $"ZERO-TRUST node proposed by untrusted content '{p.FromSource}' (Authority=None): {p.Label}");
                queue.Enqueue(zt);
            }
        }

        // 5. Verify postconditions deterministically (approval state is reflected in node Status).
        var verification = _verifier.Verify(graph, ws, userRecipients);
        foreach (var v in verification)
            audit.Add("-", "verify", $"{(v.Pass ? "PASS" : "FAIL")} {v.Id}: expected {v.Expected}; actual {v.Actual}");

        bool allVerified = verification.All(v => v.Pass);
        if (allVerified)
            foreach (var node in graph.Nodes.Where(n => n.Status == NodeStatus.Executed))
                node.Status = NodeStatus.Verified;

        return Project(prompt, resolved, graph, verification, audit, _skills.Observe(resolved.Nodes), _bundle.Lifecycle);
    }

    private static RunResult Project(string prompt, ProposedPlan resolved, IntentGraph graph,
        IReadOnlyList<VerificationResult> verification, AuditTrail audit,
        IReadOnlyList<SkillObservation> skillObs, IReadOnlyList<LifecycleStateInfo> lifecycle)
    {
        var nodes = graph.Nodes.Select(n => new NodeView(
            n.Id, n.Type, n.Label, n.Status.ToString(), n.TrustSource.ToString(), n.Authority.ToString(),
            n.SourceText, n.ParentId, n.ChildrenIds.ToList(),
            n.Action.Fields.Select(f => new FieldView(f.Field, f.Value)).ToList(), n.BlockedReason)).ToList();

        var policy = graph.Nodes.Where(n => n.Policy is not null).Select(n =>
        {
            var p = n.Policy!;
            return new PolicyView(n.Id, n.Label, p.Decision.ToString(), p.Risk, p.Reason, p.TriggeredRules,
                p.RequiresConfirmation, p.TrustSource, p.Sensitive, p.ExternalSideEffect, p.Destructive);
        }).ToList();

        var exec = graph.Nodes.Where(n => n.Execution is not null).Select(n =>
        {
            var e = n.Execution!;
            return new ExecView(n.Id, n.Label, e.Ran, e.Halted, e.Summary, e.Effects);
        }).ToList();

        var verify = verification.Select(v => new VerifyView(v.Id, v.Expected, v.Actual, v.Pass, v.Evidence)).ToList();
        var auditViews = audit.Events.Select(a => new AuditView(a.Seq, a.NodeId, a.Phase, a.Message)).ToList();

        var summary = new SummaryView(
            graph.Nodes.Count,
            graph.Nodes.Count(n => n.Status == NodeStatus.Allowed),
            graph.Nodes.Count(n => n.Status == NodeStatus.NeedsConfirmation),
            graph.Nodes.Count(n => n.Status == NodeStatus.Blocked),
            graph.Nodes.Count(n => n.Status == NodeStatus.Executed),
            graph.Nodes.Count(n => n.Status == NodeStatus.Verified));

        int OrderOf(string status) => lifecycle.FirstOrDefault(s => s.Label.Equals(status, StringComparison.OrdinalIgnoreCase))?.Order ?? 0;
        var skillItems = skillObs.Select(o => new SkillView(
            o.Skill.Id, o.Skill.Label, o.Skill.Status, o.Skill.Risk, OrderOf(o.Skill.Status),
            o.MatchedThisRun, o.Note, o.Skill.Composition, o.Skill.AllowedTools)).ToList();
        var skills = new SkillsView(lifecycle.Select(s => s.Label).ToList(), skillItems);

        return new RunResult(prompt, resolved.Fired, resolved.Unsupported, nodes, policy, exec, verify, auditViews, summary, skills);
    }

    /// <summary>Ground-truth recipients the USER requested, derived from the prompt + workspace contacts
    /// (independent of any proposer output): a known contact whose first name appears in the prompt, the
    /// external "client" when the prompt says "client", and any literal email address typed in the
    /// prompt. Mirrors the trusted resolver binding so a proposer cannot smuggle in a recipient.</summary>
    private static HashSet<string> DeriveUserRecipients(string prompt, Workspace ws)
    {
        var lowered = " " + (prompt ?? "").ToLowerInvariant() + " ";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool clientContext = lowered.Contains(" client");
        foreach (var c in ws.Contacts)
        {
            if (!c.Known) continue;
            var first = c.Name.Split(' ')[0].ToLowerInvariant();
            if (lowered.Contains(" " + first) || (clientContext && c.External))
            {
                set.Add(c.Name);
                if (!string.IsNullOrEmpty(c.Email)) set.Add(c.Email);
            }
        }
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(prompt ?? "", @"[\w.+-]+@[\w.-]+\.\w+"))
            set.Add(m.Value);
        return set;
    }
}
