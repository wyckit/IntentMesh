namespace IntentMesh.Core;

/// <summary>Where an intent node's authority originates. Only User/System can command.</summary>
public enum TrustSource { User, System, RetrievedContent, ToolOutput }

/// <summary>Authority level — the Zero-Trust rule sets RetrievedContent/ToolOutput to None.</summary>
public enum Authority { Full, None }

/// <summary>Lifecycle status of a node through the pipeline (used by the Control Room).</summary>
public enum NodeStatus { Pending, Resolved, Allowed, NeedsConfirmation, Blocked, Executed, Verified }

public static class Trust
{
    public static Authority AuthorityOf(TrustSource s) =>
        s is TrustSource.User or TrustSource.System ? Authority.Full : Authority.None;
}

/// <summary>
/// A node in the Intent Mesh. Wraps a typed action and carries everything the pipeline and the
/// Control Room need: trust provenance, policy decision, execution + verification status, and
/// graph links. Nodes derived from untrusted content inherit TrustSource=RetrievedContent.
/// </summary>
public sealed class IntentNode
{
    public required string Id { get; init; }
    public required string Type { get; init; }            // action kind (act-*)
    public required string Label { get; init; }           // human label
    public required TypedAction Action { get; init; }
    public string SourceText { get; init; } = "";         // the phrase / origin that produced it
    public TrustSource TrustSource { get; init; } = TrustSource.User;
    public Authority Authority => Trust.AuthorityOf(TrustSource);

    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public PolicyDecision? Policy { get; set; }
    public ExecutionResult? Execution { get; set; }
    public List<string> AuditRefs { get; } = new();

    public string? ParentId { get; init; }
    public List<string> ChildrenIds { get; } = new();
    public string? BlockedReason { get; set; }
}

/// <summary>The mesh: an ordered, inspectable graph of typed intent nodes.</summary>
public sealed class IntentGraph
{
    private readonly List<IntentNode> _nodes = new();
    public IReadOnlyList<IntentNode> Nodes => _nodes;

    public void Add(IntentNode node)
    {
        _nodes.Add(node);
        if (node.ParentId is { } pid && _nodes.FirstOrDefault(n => n.Id == pid) is { } parent)
            parent.ChildrenIds.Add(node.Id);
    }

    public IntentNode? ById(string id) => _nodes.FirstOrDefault(n => n.Id == id);
}
