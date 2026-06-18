using System.Net;
using System.Net.Mail;
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
// STILL NEEDS YOUR CREDENTIALS (cannot be done without them):
//   • The Gmail *API + OAuth* path (AcquireTokenAsync) needs a Google Cloud client
//     registration and consent. SMTP (incl. Gmail SMTP + app password) needs no
//     OAuth and works today. AcquireTokenAsync reads a provided GMAIL_ACCESS_TOKEN;
//     the full browser/device OAuth flow is the only part that requires your setup.
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
    /// The Gmail *API* OAuth path. It returns a token from <c>GMAIL_ACCESS_TOKEN</c> when present;
    /// the full browser/device OAuth flow (acquiring that token interactively) requires your Google
    /// Cloud client credentials and is the only piece that needs your setup. For most uses, SMTP
    /// (incl. Gmail SMTP + app password) needs no OAuth at all.
    /// </summary>
    public static Task<OAuthToken> AcquireTokenAsync()
    {
        var token = Environment.GetEnvironmentVariable("GMAIL_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "No Gmail OAuth token configured. Set GMAIL_ACCESS_TOKEN, or use SMTP (SMTP_HOST/... — " +
                "Gmail SMTP works with an app password and needs no OAuth). The interactive OAuth flow " +
                "requires your Google Cloud client credentials. See docs/INTEGRATIONS.md.");
        return Task.FromResult(new OAuthToken(token, DateTimeOffset.UtcNow.AddHours(1)));
    }
}

/// <summary>Wiring helpers: create the adapter with the default (Null) transport or a real one.</summary>
public static class OAuthAdapterWiringExample
{
    public static GmailSendAdapter CreateAdapter() => new();
    public static GmailSendAdapter CreateAdapter(IEmailTransport transport) => new(transport);
}
