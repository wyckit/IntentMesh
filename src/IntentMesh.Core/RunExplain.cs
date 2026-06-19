namespace IntentMesh.Core;

/// <summary>One node the operator must reason about: why it was blocked, or why it awaits approval.
/// Carries the policy decision, the human reason, and the rules that fired (the policy evidence).</summary>
public sealed record GatedNode(
    string NodeId, string Label, string Type, string Decision, string Risk,
    string Reason, IReadOnlyList<string> TriggeredRules);

/// <summary>How a node's outcome would change if the pending approvals were granted. <c>Changed</c>
/// is false for a hard-blocked node — proof that approval widens a Confirm, never lifts a Block.</summary>
public sealed record DecisionDelta(string NodeId, string Label, string Before, string After, bool Changed);

/// <summary>
/// The operator view of a run: the approval queue (what's gated and why), the blocked set (why it
/// can never proceed), and a what-if projection of granting every pending approval. The what-if is a
/// real second run with the gated node ids approved — so "what would approval do" is computed by the
/// same kernel, not guessed. A blocked node stays blocked in the projection, which is exactly the
/// fail-closed guarantee an operator needs to trust the queue.
/// </summary>
public sealed record RunExplanation(
    IReadOnlyList<GatedNode> Blocked,
    IReadOnlyList<GatedNode> AwaitingApproval,
    IReadOnlyList<DecisionDelta> IfApproved);

public static class RunExplain
{
    /// <param name="workspaceFactory">Produces a FRESH workspace per run, so the baseline and the
    /// what-if projection are compared on equal footing (a run mutates its workspace).</param>
    public static RunExplanation Explain(
        IntentMeshRuntime runtime, string prompt, Func<Workspace> workspaceFactory, IReadOnlySet<string>? approvals = null)
    {
        var appr = approvals ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = runtime.Run(prompt, workspaceFactory(), appr);

        var policyById = current.Policy.ToDictionary(p => p.NodeId, p => p);

        var blocked = current.Nodes
            .Where(n => n.Status == "Blocked")
            .Select(n => ToGated(n, policyById))
            .ToList();

        var awaiting = current.Nodes
            .Where(n => n.Status == "NeedsConfirmation")
            .Select(n => ToGated(n, policyById))
            .ToList();

        // What would approval do? Approve exactly the gated node ids and re-run on a fresh workspace.
        var widened = new HashSet<string>(appr, StringComparer.OrdinalIgnoreCase);
        foreach (var n in awaiting) widened.Add(n.NodeId);

        var deltas = new List<DecisionDelta>();
        if (awaiting.Count > 0)
        {
            var after = runtime.Run(prompt, workspaceFactory(), widened);
            var afterStatus = after.Nodes.ToDictionary(n => n.Id, n => n.Status);
            foreach (var n in awaiting)
            {
                var to = afterStatus.TryGetValue(n.NodeId, out var s) ? s : "(absent)";
                deltas.Add(new DecisionDelta(n.NodeId, n.Label, "NeedsConfirmation", to, Changed: to != "NeedsConfirmation"));
            }
        }

        return new RunExplanation(blocked, awaiting, deltas);
    }

    private static GatedNode ToGated(NodeView n, IReadOnlyDictionary<string, PolicyView> policyById)
    {
        var p = policyById.TryGetValue(n.Id, out var pv) ? pv : null;
        return new GatedNode(
            n.Id, n.Label, n.Type,
            Decision: p?.Decision ?? (n.Status == "Blocked" ? "Block" : "Confirm"),
            Risk: p?.Risk ?? "",
            Reason: p?.Reason ?? n.BlockedReason ?? "(no policy reason recorded)",
            TriggeredRules: p?.TriggeredRules ?? Array.Empty<string>());
    }
}
