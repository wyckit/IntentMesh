using System.Text;
using System.Text.Json;

namespace IntentMesh.Core;

/// <summary>
/// A real LLM behind the <see cref="IIntentProposer"/> seam. It asks an <see cref="ILlmClient"/> to
/// turn the prompt into a JSON list of typed actions, then realizes each into a registry-bounded
/// <see cref="IntentNode"/>. The model is UNTRUSTED — its output is gated by the PolicyGate exactly
/// like the rule-based resolver's. The proposer enforces the kernel invariants itself:
///
/// <list type="bullet">
///   <item><b>Translation-Drift</b>: an action whose kind is not in the registry is dropped to
///   <c>Unsupported</c>, never emitted — the LLM cannot invent a contract.</item>
///   <item><b>Fail-closed</b>: malformed / non-JSON output yields zero nodes (and a diagnostic),
///   never an exception and never an untyped action.</item>
///   <item>Every emitted node carries <c>TrustSource.User</c> (the agent's planner proposes as the
///   user) — which still means a send/delete/external action is gated, as FrameworkTests prove.</item>
/// </list>
/// </summary>
public sealed class LlmIntentProposer : IIntentProposer
{
    private readonly SymbolicBundle _bundle;
    private readonly ILlmClient _llm;

    public LlmIntentProposer(SymbolicBundle bundle, ILlmClient llm)
    {
        _bundle = bundle;
        _llm = llm;
    }

    public ProposedPlan Propose(string prompt, Workspace ws)
    {
        var fired = new List<string>();
        var unsupported = new List<string>();
        var nodes = new List<IntentNode>();

        string raw;
        try { raw = _llm.Complete(BuildSystemPrompt(), prompt); }
        catch (Exception ex)
        {
            unsupported.Add($"LLM call failed: {ex.Message}");
            return new ProposedPlan(nodes, fired, unsupported);
        }

        if (!TryExtractActions(raw, out var proposed))
        {
            unsupported.Add("LLM output was not parseable as a JSON action list — proposing nothing (fail-closed).");
            return new ProposedPlan(nodes, fired, unsupported);
        }

        int n = 0;
        foreach (var (kind, paramFields) in proposed)
        {
            // Translation-Drift guard: refuse any kind the registry doesn't define.
            if (!_bundle.IsRegistered(kind))
            {
                unsupported.Add($"LLM proposed unregistered kind '{kind}' — refusing to emit (Translation-Drift).");
                continue;
            }
            // Fail-closed on malformed/ambiguous intent: a side-effecting action missing a
            // target/safety field (recipient, path, command, table) is dropped — never defaulted into
            // a gated executable node.
            var missing = TypedActionFactory.RequiredFields(kind).Where(rf => !TypedActionFactory.HasValue(paramFields, rf)).ToList();
            if (missing.Count > 0)
            {
                unsupported.Add($"LLM proposed '{kind}' missing required field(s): {string.Join(", ", missing)} — refusing to emit (fail-closed).");
                continue;
            }
            var action = TypedActionFactory.Build(kind, paramFields);
            if (action is null)
            {
                unsupported.Add($"no typed action builder for '{kind}' — refusing to emit.");
                continue;
            }
            fired.Add($"{kind} <- llm");
            nodes.Add(new IntentNode
            {
                Id = $"n{++n}",
                Type = kind,
                Label = $"LLM-proposed {kind}",
                Action = action,
                SourceText = "llm-proposer",
                TrustSource = TrustSource.User,   // the planner proposes AS the user — still gated
                Status = NodeStatus.Resolved,
            });
        }

        return new ProposedPlan(nodes, fired, unsupported);
    }

    /// <summary>The system prompt: enumerate the registered kinds + their fields so the model emits
    /// only contracts the registry knows, and pin the output to a strict JSON shape.</summary>
    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the proposal layer of IntentMesh, a verified-intent runtime. Translate the user's");
        sb.AppendLine("request into a list of TYPED actions chosen ONLY from the registry below. Never invent a kind.");
        sb.AppendLine("You have no authority to execute — a policy gate validates everything you propose.");
        sb.AppendLine();
        sb.AppendLine("Registered action kinds (kind — fields):");
        foreach (var c in _bundle.Contracts.Values.OrderBy(c => c.Kind, StringComparer.Ordinal))
            sb.AppendLine($"  {c.Kind} — {(c.Fields.Count > 0 ? string.Join(", ", c.Fields) : "(no fields)")}");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object of this exact shape (no prose, no markdown fence):");
        sb.AppendLine("{\"actions\":[{\"kind\":\"act-...\",\"fields\":{\"fieldName\":\"value\"}}]}");
        sb.AppendLine("Use only kinds from the registry. Omit fields you don't know. If nothing applies, return {\"actions\":[]}.");
        return sb.ToString();
    }

    /// <summary>Parse the model's text into (kind, fields) pairs. Tolerant of a leading/trailing
    /// prose or a ```json fence by scanning for the first JSON object; fail-closed on anything it
    /// can't read.</summary>
    private static bool TryExtractActions(string raw, out List<(string Kind, Dictionary<string, string> Fields)> result)
    {
        result = new();
        var json = ExtractJsonObject(raw);
        if (json is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var a in actions.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                if (!a.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String) continue;
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (a.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
                    foreach (var prop in f.EnumerateObject())
                        fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? ""
                            : prop.Value.GetRawText();   // arrays/numbers kept as raw JSON for the factory
                result.Add((k.GetString()!, fields));
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Return the substring from the first '{' to its matching '}', honoring strings — so a
    /// fenced or prose-wrapped JSON object is still recovered. Null if none.</summary>
    private static string? ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; char quote = '\0';
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == quote && text[i - 1] != '\\') quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }
        return null;
    }
}
