namespace IntentMesh.Core;

/// <summary>One chronological, human-readable decision record — the agentic analog of --trace.</summary>
public sealed record AuditEvent(int Seq, string NodeId, string Phase, string Message);

/// <summary>The audit trail: every resolve / policy / execute / verify decision, in order.</summary>
public sealed class AuditTrail
{
    private readonly List<AuditEvent> _events = new();
    public IReadOnlyList<AuditEvent> Events => _events;

    public void Add(string nodeId, string phase, string message)
        => _events.Add(new AuditEvent(_events.Count + 1, nodeId, phase, message));
}
