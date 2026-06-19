namespace IntentMesh.Core;

/// <summary>
/// The LLM transport seam for <see cref="LlmIntentProposer"/>. Injectable so the proposer's logic
/// (prompt construction, JSON parsing, Translation-Drift enforcement) is unit-testable offline with a
/// scripted client — the real network impl (AnthropicLlmClient) lives behind this interface, exactly
/// as the MCP and OAuth transports do. The LLM is UNTRUSTED: whatever it returns is gated by the
/// PolicyGate like any other proposal.
/// </summary>
public interface ILlmClient
{
    /// <summary>Run one completion. Returns the model's raw text (expected to be JSON the proposer
    /// parses fail-closed).</summary>
    string Complete(string systemPrompt, string userPrompt);

    /// <summary>A short provenance tag for proposals from this client (e.g.
    /// <c>anthropic:claude-haiku-4-5</c>), stamped onto emitted nodes so a proposed intent's origin is
    /// traceable in the graph and audit. Defaults to <c>llm</c>.</summary>
    string Provenance => "llm";
}
