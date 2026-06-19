using System.Text;
using System.Text.Json;
using IntentMesh.Core;

namespace IntentMesh.Integrations;

/// <summary>
/// A real <see cref="ILlmClient"/> for the Anthropic Messages API — dependency-free
/// (<see cref="HttpClient"/> + System.Text.Json, no SDK, consistent with the repo's zero-NuGet
/// ethos). It POSTs <c>/v1/messages</c> with the required headers and returns the concatenated
/// text content. Used by <see cref="LlmIntentProposer"/> when an API key is configured; tests inject
/// a scripted <see cref="ILlmClient"/> instead, so the proposer logic runs offline.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient, IDisposable
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    /// <summary>Cheap, fast model — right for structured NL→typed-action extraction.</summary>
    public const string DefaultModel = "claude-haiku-4-5";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;

    public AnthropicLlmClient(string apiKey, string? model = null, int maxTokens = 1024, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!;
        _maxTokens = maxTokens;
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout };
    }

    /// <summary>Build from the environment: <c>ANTHROPIC_API_KEY</c> (required) and optional
    /// <c>ANTHROPIC_MODEL</c>. Returns null when no key is set, so callers can fall back to the
    /// rule-based proposer without a hard dependency on credentials.</summary>
    public static AnthropicLlmClient? FromEnvironment(HttpClient? http = null)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        return new AnthropicLlmClient(key!, Environment.GetEnvironmentVariable("ANTHROPIC_MODEL"), http: http);
    }

    /// <summary>Provenance tag recording which model proposed (e.g. <c>anthropic:claude-haiku-4-5</c>).</summary>
    public string Provenance => $"anthropic:{_model}";

    public string Complete(string systemPrompt, string userPrompt)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = _maxTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        using var resp = _http.Send(req);
        var payload = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic API error {(int)resp.StatusCode}: {Truncate(payload, 400)}");

        // Concatenate every text content block.
        using var doc = JsonDocument.Parse(payload);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    sb.Append(txt.GetString());
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
