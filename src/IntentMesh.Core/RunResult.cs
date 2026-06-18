namespace IntentMesh.Core;

// Serializable projection of a pipeline run — consumed by the Web Control Room (JSON) and the
// CLI --trace renderer. Plain records so System.Text.Json output is clean and stable.

public sealed record FieldView(string Field, string Value);

public sealed record NodeView(
    string Id, string Type, string Label, string Status,
    string TrustSource, string Authority, string SourceText,
    string? ParentId, IReadOnlyList<string> Children,
    IReadOnlyList<FieldView> Fields, string? BlockedReason);

public sealed record PolicyView(
    string NodeId, string Label, string Decision, string Risk, string Reason,
    IReadOnlyList<string> TriggeredRules, bool RequiresConfirmation,
    string TrustSource, bool Sensitive, bool ExternalSideEffect, bool Destructive);

public sealed record ExecView(string NodeId, string Label, bool Ran, bool Halted, string Summary, IReadOnlyList<string> Effects);

public sealed record VerifyView(string Id, string Expected, string Actual, bool Pass, string Evidence);

public sealed record AuditView(int Seq, string NodeId, string Phase, string Message);

public sealed record SummaryView(int Total, int Allowed, int NeedsConfirmation, int Blocked, int Executed, int Verified);

public sealed record RunResult(
    string Prompt,
    IReadOnlyList<string> ResolverFired,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<NodeView> Nodes,
    IReadOnlyList<PolicyView> Policy,
    IReadOnlyList<ExecView> Execution,
    IReadOnlyList<VerifyView> Verification,
    IReadOnlyList<AuditView> Audit,
    SummaryView Summary);
