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
}
