using System.Net;
using System.Net.Mail;
using System.Text.Json;
using IntentMesh.Core;

namespace IntentMesh.Integrations;

// ──────────────────────────────────────────────────────────────────────────────
// Real email adapter — a working adapter behind the IntentMesh IToolAdapter seam.
//
// NOW REAL (converted from the Phase 5 prototype stub):
//   • IEmailTransport + SmtpEmailTransport: a real SMTP sender (System.Net.Mail,
//     no third-party dependency). When SMTP_* env vars are configured it transmits
//     for real — including Gmail's SMTP with an app password. Unconfigured, it
//     falls back to a NullEmailTransport that records but sends nothing.
//   • GmailSendAdapter takes an IEmailTransport and, on approval, calls it for real.
//
// NOW REAL (the OAuth device flow is implemented):
//   • GoogleDeviceCodeFlow runs the OAuth 2.0 Device Authorization Grant (RFC 8628)
//     against Google's endpoints with the built-in HttpClient (no Google SDK): request
//     a device+user code, show the user the verification URL, poll the token endpoint
//     (honouring authorization_pending / slow_down) until consent, then return a real
//     access token. AcquireTokenAsync uses it when GOOGLE_OAUTH_CLIENT_ID +
//     GOOGLE_OAUTH_CLIENT_SECRET are set.
//
// STILL NEEDS YOUR CREDENTIALS (cannot be done without them):
//   • Running the flow needs YOUR Google Cloud OAuth client id + secret and a human to
//     approve the consent screen — that is inherent to OAuth, not a stub. Without them,
//     AcquireTokenAsync reads a provided GMAIL_ACCESS_TOKEN, and SMTP (incl. Gmail SMTP
//     + app password) needs no OAuth at all.
//
// The IntentMesh pipeline still governs everything: the PolicyGate blocks this
// adapter unless the 'email' capability is granted (pol-capability-not-granted),
// and the adapter transmits nothing unless the node was explicitly approved.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A pluggable email transport. The adapter calls this only after IntentMesh approves.</summary>
public interface IEmailTransport
{
    /// <summary>Returns true if a message was actually transmitted over the network.</summary>
    Task<bool> SendAsync(string to, string subject, string body);
    string Describe();
}

/// <summary>Records sends in-process; performs no network I/O. The default and the test/sandbox transport.</summary>
public sealed class NullEmailTransport : IEmailTransport
{
    public List<(string To, string Subject, string Body)> Sent { get; } = new();
    public Task<bool> SendAsync(string to, string subject, string body) { Sent.Add((to, subject, body)); return Task.FromResult(false); }
    public string Describe() => "null transport (records only; no network)";
}

/// <summary>
/// A REAL SMTP transport using the built-in System.Net.Mail client (no dependency). It transmits
/// for real when constructed with a host — including Gmail's SMTP (smtp.gmail.com:587) using an
/// app password, which needs no OAuth. Build one from env with <see cref="FromEnvironment"/>.
/// </summary>
public sealed class SmtpEmailTransport : IEmailTransport
{
    private readonly string _host, _from;
    private readonly int _port;
    private readonly string? _user, _pass;
    private readonly bool _ssl;

    public SmtpEmailTransport(string host, int port, string from, string? user = null, string? pass = null, bool ssl = true)
    { _host = host; _port = port; _from = from; _user = user; _pass = pass; _ssl = ssl; }

    public async Task<bool> SendAsync(string to, string subject, string body)
    {
        using var client = new SmtpClient(_host, _port) { EnableSsl = _ssl };
        if (_user is not null) client.Credentials = new NetworkCredential(_user, _pass);
        using var msg = new MailMessage(_from, to, subject, body);
        await client.SendMailAsync(msg);
        return true;
    }

    public string Describe() => $"SMTP {_host}:{_port}";

    /// <summary>Build a real SMTP transport from env (SMTP_HOST, SMTP_PORT, SMTP_FROM, SMTP_USER,
    /// SMTP_PASS, SMTP_SSL). Returns a <see cref="NullEmailTransport"/> when SMTP_HOST is unset.</summary>
    public static IEmailTransport FromEnvironment()
    {
        var host = Environment.GetEnvironmentVariable("SMTP_HOST");
        if (string.IsNullOrWhiteSpace(host)) return new NullEmailTransport();
        int port = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587;
        var user = Environment.GetEnvironmentVariable("SMTP_USER");
        var from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? user ?? "noreply@intentmesh.local";
        var pass = Environment.GetEnvironmentVariable("SMTP_PASS");
        bool ssl = !string.Equals(Environment.GetEnvironmentVariable("SMTP_SSL"), "false", StringComparison.OrdinalIgnoreCase);
        return new SmtpEmailTransport(host, port, from, user, pass, ssl);
    }
}

/// <summary>The token returned by the (configured) Gmail OAuth path.</summary>
public sealed record OAuthToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// A real email adapter on the IntentMesh <see cref="IToolAdapter"/> seam. It declares the
/// <c>email</c> capability (the PolicyGate blocks it unless granted), requires explicit approval,
/// and on approval transmits via its <see cref="IEmailTransport"/> — really sending when SMTP is
/// configured. Named GmailSendAdapter because Gmail SMTP (app password) is the common real target.
/// </summary>
public sealed class GmailSendAdapter : IToolAdapter
{
    /// <summary>The capability the PolicyGate checks before this adapter is reached.</summary>
    public const string RequiredCapability = "email";

    private readonly IEmailTransport _transport;
    public GmailSendAdapter(IEmailTransport? transport = null) => _transport = transport ?? new NullEmailTransport();

    public bool Handles(string kind) => kind == Kinds.SendEmail;

    public ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws, bool approved)
    {
        if (node.Action is not SendEmailAction send)
            return ToolHost.Ok(node.Id, "GmailSendAdapter: no-op (unexpected action type)");

        // Approval gate (capability 'email' is enforced upstream by the PolicyGate).
        if (!approved)
            return ToolHost.Halt(node.Id,
                $"send to {send.Recipient} requires explicit user approval — NOT transmitted.",
                "0 messages transmitted");

        // REAL send via the configured transport.
        bool transmitted;
        try { transmitted = _transport.SendAsync(send.Recipient, send.DraftRef, string.Join(",", send.BodySourceRefs)).GetAwaiter().GetResult(); }
        catch (Exception ex)
        { return ToolHost.Halt(node.Id, $"transport error sending to {send.Recipient}: {ex.Message}", "0 messages transmitted"); }

        ws.SentEmails.Add(send.Recipient);
        return ToolHost.Ok(node.Id,
            transmitted
                ? $"Sent to {send.Recipient} via {_transport.Describe()}."
                : $"Recorded send to {send.Recipient} ({_transport.Describe()}). Set SMTP_* env to transmit for real.",
            transmitted ? "1 message transmitted" : "0 messages transmitted (no SMTP configured)",
            $"recipient = {send.Recipient}");
    }

    /// <summary>
    /// Acquire a Gmail API access token. Resolution order: (1) a pre-supplied
    /// <c>GMAIL_ACCESS_TOKEN</c>; (2) the real OAuth 2.0 device flow when
    /// <c>GOOGLE_OAUTH_CLIENT_ID</c> + <c>GOOGLE_OAUTH_CLIENT_SECRET</c> are set
    /// (<c>GOOGLE_OAUTH_SCOPE</c> optional) — it prints the verification URL + user code and polls
    /// until you consent; (3) otherwise a clear config error. For most uses, SMTP (incl. Gmail SMTP
    /// + app password) needs no OAuth at all.
    /// </summary>
    public static async Task<OAuthToken> AcquireTokenAsync()
    {
        var token = Environment.GetEnvironmentVariable("GMAIL_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            return new OAuthToken(token, DateTimeOffset.UtcNow.AddHours(1));

        var clientId = Environment.GetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
        {
            var scope = Environment.GetEnvironmentVariable("GOOGLE_OAUTH_SCOPE") ?? GoogleDeviceCodeFlow.DefaultScope;
            var flow = new GoogleDeviceCodeFlow();
            return await flow.AuthorizeAsync(clientId!, clientSecret!, scope, prompt: d =>
                Console.Error.WriteLine($"[IntentMesh] To authorize Gmail send, visit {d.VerificationUrl} and enter code: {d.UserCode}"));
        }

        throw new InvalidOperationException(
            "No Gmail OAuth token configured. Set GMAIL_ACCESS_TOKEN, or set GOOGLE_OAUTH_CLIENT_ID + " +
            "GOOGLE_OAUTH_CLIENT_SECRET to run the interactive OAuth device flow, or use SMTP " +
            "(SMTP_HOST/... — Gmail SMTP works with an app password and needs no OAuth). See docs/INTEGRATIONS.md.");
    }
}

/// <summary>The server's response to a device-authorization request (RFC 8628 §3.2).</summary>
public sealed record DeviceCodeResponse(
    string DeviceCode, string UserCode, string VerificationUrl, int Interval, int ExpiresIn);

/// <summary>
/// A real, dependency-free OAuth 2.0 <b>Device Authorization Grant</b> (RFC 8628) client for
/// Google. It uses only <see cref="HttpClient"/> + System.Text.Json — no Google SDK. The flow:
/// request a device code, show the user the verification URL + user code, then poll the token
/// endpoint (respecting <c>authorization_pending</c> and <c>slow_down</c>) until the user consents.
/// The polling/delay and HTTP handler are injectable, so the state machine is unit-testable without
/// real network or real waiting.
/// </summary>
public sealed class GoogleDeviceCodeFlow
{
    /// <summary>The minimal scope needed to send mail via the Gmail API.</summary>
    public const string DefaultScope = "https://www.googleapis.com/auth/gmail.send";

    private const string DeviceCodeEndpoint = "https://oauth2.googleapis.com/device/code";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly HttpClient _http;
    public GoogleDeviceCodeFlow(HttpClient? http = null) => _http = http ?? new HttpClient();

    /// <summary>Run the whole flow: request a device code, surface it via <paramref name="prompt"/>,
    /// then poll until a token is issued.</summary>
    public async Task<OAuthToken> AuthorizeAsync(
        string clientId, string clientSecret, string scope,
        Action<DeviceCodeResponse>? prompt = null,
        Func<int, Task>? delay = null,
        CancellationToken ct = default)
    {
        var device = await RequestDeviceCodeAsync(clientId, scope, ct);
        prompt?.Invoke(device);
        return await PollForTokenAsync(clientId, clientSecret, device, delay, ct);
    }

    /// <summary>Step 1 — request a device + user code (RFC 8628 §3.1–3.2).</summary>
    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(string clientId, string scope, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = scope,
        });
        using var resp = await _http.PostAsync(DeviceCodeEndpoint, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"Device-code request failed: {err.GetString()}");

        // Google returns "verification_url"; the RFC standard field is "verification_uri".
        string verify = Str(root, "verification_uri") ?? Str(root, "verification_url") ?? "";
        return new DeviceCodeResponse(
            DeviceCode: Str(root, "device_code") ?? throw new InvalidOperationException("device_code missing"),
            UserCode: Str(root, "user_code") ?? "",
            VerificationUrl: verify,
            Interval: Int(root, "interval", 5),
            ExpiresIn: Int(root, "expires_in", 1800));
    }

    /// <summary>Step 2 — poll the token endpoint until consent (RFC 8628 §3.4–3.5). Handles
    /// <c>authorization_pending</c> (keep waiting) and <c>slow_down</c> (back off by 5s); any other
    /// error is terminal.</summary>
    public async Task<OAuthToken> PollForTokenAsync(
        string clientId, string clientSecret, DeviceCodeResponse device,
        Func<int, Task>? delay = null, CancellationToken ct = default)
    {
        delay ??= ms => Task.Delay(ms, ct);
        int intervalSeconds = Math.Max(1, device.Interval);
        int elapsed = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = DeviceGrantType,
            });
            using var resp = await _http.PostAsync(TokenEndpoint, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct); // error responses are 4xx with a JSON body
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (Str(root, "access_token") is { } access)
                return new OAuthToken(access, DateTimeOffset.UtcNow.AddSeconds(Int(root, "expires_in", 3600)));

            var error = Str(root, "error");
            switch (error)
            {
                case "authorization_pending":
                    break;                       // keep waiting at the current interval
                case "slow_down":
                    intervalSeconds += 5;        // RFC 8628 §3.5: back off
                    break;
                default:
                    throw new InvalidOperationException($"Device token poll failed: {error ?? "unknown error"}");
            }

            if (elapsed >= device.ExpiresIn)
                throw new TimeoutException("Device authorization expired before the user consented.");
            await delay(intervalSeconds * 1000);
            elapsed += intervalSeconds;
        }
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string key, int fallback) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}

/// <summary>Wiring helpers: create the adapter with the default (Null) transport or a real one.</summary>
public static class OAuthAdapterWiringExample
{
    public static GmailSendAdapter CreateAdapter() => new();
    public static GmailSendAdapter CreateAdapter(IEmailTransport transport) => new(transport);
}
