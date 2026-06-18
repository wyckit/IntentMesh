namespace IntentMesh.Core;

/// <summary>
/// The IntentMesh pipeline orchestrator. Generalizes PassGen's resolve -> validate-before-generate
/// -> generate -> check flow to many tools:
///
///   prompt -> IntentResolver -> Intent Mesh -> PolicyGate -> tool adapters -> PostconditionVerifier -> AuditTrail
///
/// Authority moves downstream, away from language. Nodes a tool proposes from untrusted content
/// are ingested as ZERO-TRUST and re-run through the SAME gate — which blocks them.
/// </summary>
public sealed class IntentMeshRuntime
{
    private readonly SymbolicBundle _bundle;
    private readonly IntentResolver _resolver;
    private readonly PolicyGate _gate;
    private readonly ToolHost _tools = new();
    private readonly PostconditionVerifier _verifier = new();
    private readonly SkillProposer _skills;

    public SymbolicBundle Bundle => _bundle;

    public IntentMeshRuntime(SymbolicBundle bundle)
    {
        _bundle = bundle;
        _resolver = new IntentResolver(bundle);
        _gate = new PolicyGate(bundle);
        _skills = new SkillProposer(bundle.Skills);
    }

    public static IntentMeshRuntime Load(string? compiledDir = null)
        => new(SymbolicBundle.Load(compiledDir ?? DatasetLocator.FindCompiledDir()));

    public RunResult Run(string prompt, Workspace ws) => Run(prompt, ws, new HashSet<string>());

    /// <param name="approvals">Node ids the user has explicitly approved. An approval only ever
    /// applies to a full-authority node whose decision is Confirm — it can NEVER turn a Block into
    /// execution, so a zero-trust / injected node remains blocked regardless of what is passed.</param>
    public RunResult Run(string prompt, Workspace ws, IReadOnlySet<string> approvals)
    {
        var graph = new IntentGraph();
        var audit = new AuditTrail();

        // 1. Resolve language -> typed, registry-bounded intent nodes (all TrustSource.User).
        var resolved = _resolver.Resolve(prompt, ws);
        foreach (var node in resolved.Nodes)
        {
            graph.Add(node);
            audit.Add(node.Id, "resolve", $"resolved {node.Type} -> {node.Label}");
        }
        foreach (var u in resolved.Unsupported)
            audit.Add("-", "resolve", $"unsupported: {u}");

        // Recipients the USER explicitly named (used to detect substitution from untrusted content).
        var userRecipients = resolved.Nodes
            .Select(n => n.Action)
            .OfType<DraftEmailAction>().Select(a => a.Recipient)
            .Concat(resolved.Nodes.Select(n => n.Action).OfType<SendEmailAction>().Select(a => a.Recipient))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ctx = new PolicyContext(ws, userRecipients);

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
            bool approved = decision.Decision == Decision.Confirm
                            && node.Authority == Authority.Full
                            && approvals.Contains(node.Id);

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
            if (exec.Ran && !exec.Halted && (decision.Decision == Decision.Allow || approved))
                node.Status = NodeStatus.Executed;

            // Ingest proposed nodes as ZERO-TRUST (State-Poisoning guard) and re-queue them.
            foreach (var p in exec.Proposed)
            {
                var zt = new IntentNode
                {
                    Id = $"n{++counter}", Type = p.Type, Label = p.Label, Action = p.Action,
                    SourceText = p.SourceText, TrustSource = TrustSource.RetrievedContent,
                    Status = NodeStatus.Resolved, ParentId = node.Id
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

    private static RunResult Project(string prompt, IntentResolver.Result resolved, IntentGraph graph,
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
}
