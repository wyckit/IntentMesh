using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IntentMesh.Integrations;

/// <summary>
/// A real MCP client over the <b>Streamable HTTP</b> transport — the sibling of
/// <see cref="McpStdioClient"/>. It POSTs JSON-RPC 2.0 messages to a single MCP endpoint and reads
/// the reply whether the server answers with <c>application/json</c> (a single response) or
/// <c>text/event-stream</c> (Server-Sent Events). It runs the MCP handshake
/// (initialize + notifications/initialized), honours the <c>Mcp-Session-Id</c> the server assigns on
/// initialize, and exposes tools/list + tools/call. No third-party SDK — just
/// <see cref="HttpClient"/> + System.Text.Json.
///
/// <para>The McpProxy forwards to this only after IntentMesh approves the intent; the gate is
/// transport-agnostic, so HTTP/SSE is governed exactly like stdio.</para>
/// </summary>
public sealed class McpHttpClient : IMcpClient
{
    /// <summary>Protocol revision that introduced the Streamable HTTP transport.</summary>
    public const string ProtocolVersion = "2025-03-26";

    // Resource bounds against a hostile/compromised server.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private const long MaxResponseBytes = 8L * 1024 * 1024;   // 8 MiB
    private const int MaxSseEvents = 10_000;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly bool _ownsClient;
    private string? _sessionId;
    private int _id;

    private McpHttpClient(HttpClient http, Uri endpoint, bool ownsClient)
    {
        _http = http;
        _endpoint = endpoint;
        _ownsClient = ownsClient;
    }

    /// <summary>Connect to an MCP server over Streamable HTTP at <paramref name="endpointUrl"/> and
    /// run the initialize handshake. The endpoint is validated against SSRF: scheme must be
    /// http/https, the cloud-metadata address (169.254.169.254) is always blocked, and a non-loopback
    /// host must use https unless <paramref name="allowInsecureTransport"/> is set. Pass your own
    /// <see cref="HttpClient"/> to control proxies/auth/test handlers; otherwise one is created (with
    /// a read timeout + response-size cap) and disposed with this client. Throws if the server fails
    /// to respond to initialize.</summary>
    public static McpHttpClient Connect(string endpointUrl, HttpClient? http = null, bool allowInsecureTransport = false)
    {
        var uri = new Uri(endpointUrl);
        ValidateEndpoint(uri, allowInsecureTransport);
        bool owns = http is null;
        http ??= CreateGuardedClient(uri.IsLoopback);
        return Initialize(new McpHttpClient(http, uri, owns));
    }

    /// <summary>The default internal client, hardened against SSRF beyond the Connect-time check:
    /// redirects are NOT followed (a 3xx to an internal target can't be chased), and every actual TCP
    /// connection re-validates its resolved IP against the internal-range blocklist — so DNS that
    /// re-resolves to an internal address between preflight and send (rebinding) is still blocked.
    /// A caller-supplied HttpClient is used as-is (the Connect-time validation still applies).</summary>
    private static HttpClient CreateGuardedClient(bool loopbackAllowed)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = ReadTimeout,
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;
                IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
                    ? new[] { literal }
                    : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                foreach (var ip in addresses)
                {
                    if (!loopbackAllowed && IsInternalAddress(ip)) continue;   // re-validate at connect time
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(ip, port, ct).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                }
                throw new InvalidOperationException($"MCP endpoint host '{host}' resolves only to internal/blocked addresses — blocked (SSRF guard).");
            },
        };
        return new HttpClient(handler) { Timeout = ReadTimeout, MaxResponseContentBufferSize = MaxResponseBytes };
    }

    private static McpHttpClient Initialize(McpHttpClient client)
    {
        client.Request("initialize", new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "intentmesh", version = "1.0.0" }
        });
        client.Notify("notifications/initialized");
        return client;
    }

    /// <summary>SSRF guard: only http/https; https required for any non-loopback host (unless
    /// overridden); and — crucially — the host is RESOLVED and any private / loopback / link-local /
    /// ULA / cloud-metadata address is blocked, so a hostname that points at an internal target
    /// (DNS rebinding, metadata.* names, IPv4-mapped IPv6, etc.) can't slip past a literal-string
    /// check. Loopback is permitted only when the host is literally a loopback name/IP.</summary>
    private static void ValidateEndpoint(Uri uri, bool allowInsecureTransport)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"MCP endpoint scheme '{uri.Scheme}' is not allowed (http/https only).");

        bool loopbackLiteral = uri.IsLoopback;
        if (!loopbackLiteral && uri.Scheme != Uri.UriSchemeHttps && !allowInsecureTransport)
            throw new InvalidOperationException("MCP endpoint must use https for non-loopback hosts (set allowInsecureTransport to override).");

        // Resolve the host to addresses and block internal ranges. A literally-loopback endpoint
        // (localhost / 127.0.0.1 / [::1]) is allowed; anything else that resolves internal is blocked.
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal)) addresses = new[] { literal };
        else
        {
            try { addresses = Dns.GetHostAddresses(uri.Host); }
            catch { throw new InvalidOperationException($"MCP endpoint host '{uri.Host}' could not be resolved — blocked."); }
        }
        foreach (var ip in addresses)
            if (!loopbackLiteral && IsInternalAddress(ip))
                throw new InvalidOperationException($"MCP endpoint resolves to an internal/blocked address ({ip}) — blocked (SSRF guard).");
    }

    /// <summary>True for loopback, RFC1918 private, CGNAT, link-local (incl. 169.254 cloud metadata),
    /// IPv6 link-local/ULA, and IPv4-mapped equivalents.</summary>
    private static bool IsInternalAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0 || b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)              // link-local incl. 169.254.169.254 metadata
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT 100.64/10
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv4MappedToIPv6) return IsInternalAddress(ip.MapToIPv4());
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC;                     // ULA fc00::/7
        }
        return false;
    }

    /// <summary>tools/list — the names of the tools the server exposes.</summary>
    public IReadOnlyList<string> ListTools()
    {
        var result = Request("tools/list", new { });
        var names = new List<string>();
        if (result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                if (t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    names.Add(n.GetString()!);
        return names;
    }

    /// <summary>tools/call — invoke a tool and return the raw JSON result. Only the McpProxy calls
    /// this, and only after IntentMesh has approved the intent.</summary>
    public string CallTool(string name, IReadOnlyDictionary<string, string> arguments)
        => Request("tools/call", new { name, arguments }).GetRawText();

    // ── JSON-RPC over HTTP (single endpoint; JSON or SSE reply) ────────────────
    private JsonElement Request(string method, object @params)
    {
        int id = ++_id;
        using var resp = Post(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }));

        // The server assigns a session on initialize; echo it on every later request.
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid))
            _sessionId = sid.FirstOrDefault() ?? _sessionId;

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        return mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            ? ReadFromEventStream(resp, id)
            : ReadFromJson(resp, id);
    }

    private void Notify(string method)
    {
        // Notifications carry no id; the server typically replies 202 Accepted with no body.
        using var _ = Post(JsonSerializer.Serialize(new { jsonrpc = "2.0", method }));
    }

    private HttpResponseMessage Post(string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (_sessionId is not null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        var resp = _http.Send(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            int status = (int)resp.StatusCode;                 // capture BEFORE dispose
            // Read the error body through the SAME bounded reader (size cap + deadline) so a hostile
            // endpoint can't stream a huge / never-ending error body to bypass the success-path caps.
            string body = "";
            try { body = ReadCapped(resp); } catch { body = "(error body unavailable or exceeded cap)"; }
            resp.Dispose();
            throw new InvalidOperationException($"MCP HTTP error {status}: {Truncate(body, 500)}");
        }
        return resp;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…[truncated]";

    /// <summary>Single JSON-RPC response in the body, read with an app-level size cap + deadline so
    /// the bound holds even when the caller supplied their own (uncapped) HttpClient.</summary>
    private static JsonElement ReadFromJson(HttpResponseMessage resp, int id)
    {
        var body = ReadCapped(resp);
        if (string.IsNullOrWhiteSpace(body))
            throw new IOException("MCP server returned an empty body for a request.");
        using var doc = JsonDocument.Parse(body);
        return ExtractResult(doc.RootElement, id);
    }

    /// <summary>Read a response body to a hard byte cap under a read deadline.</summary>
    private static string ReadCapped(HttpResponseMessage resp)
    {
        using var stream = resp.Content.ReadAsStream();
        using var cts = new CancellationTokenSource(ReadTimeout);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        long total = 0;
        int read;
        while ((read = stream.ReadAsync(buffer.AsMemory(), cts.Token).AsTask().GetAwaiter().GetResult()) > 0)
        {
            total += read;
            if (total > MaxResponseBytes) throw new IOException("MCP response exceeded the size cap.");
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>SSE reply: read <c>data:</c> events until the one whose id matches arrives. Bounded
    /// by a read deadline, a total-byte cap, and an event-count cap so a hostile server that streams
    /// forever (slow-loris) or floods bytes cannot hang the thread or exhaust memory.</summary>
    private static JsonElement ReadFromEventStream(HttpResponseMessage resp, int id)
    {
        using var stream = resp.Content.ReadAsStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var cts = new CancellationTokenSource(ReadTimeout);
        var data = new StringBuilder();
        long total = 0;
        int events = 0;
        string? line;
        while ((line = reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult()) is not null)
        {
            total += line.Length + 1;
            if (total > MaxResponseBytes) throw new IOException("MCP SSE response exceeded the size cap.");
            if (line.Length == 0)
            {
                // Blank line terminates an SSE event — try to match it.
                if (data.Length > 0)
                {
                    if (++events > MaxSseEvents) throw new IOException("MCP SSE event count exceeded the cap.");
                    using var doc = JsonDocument.Parse(data.ToString());
                    if (TryExtractResult(doc.RootElement, id, out var result)) return result;
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
            // other SSE fields (event:, id:, retry:, comments) are ignored
        }
        // Stream ended — flush any trailing event without a terminating blank line.
        if (data.Length > 0)
        {
            using var doc = JsonDocument.Parse(data.ToString());
            if (TryExtractResult(doc.RootElement, id, out var result)) return result;
        }
        throw new IOException($"MCP SSE stream closed before a response for request id {id} arrived.");
    }

    private static JsonElement ExtractResult(JsonElement message, int id)
        => TryExtractResult(message, id, out var result)
            ? result
            : throw new InvalidOperationException($"No JSON-RPC response for request id {id}.");

    private static bool TryExtractResult(JsonElement message, int id, out JsonElement result)
    {
        result = default;
        if (!message.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || idEl.GetInt32() != id)
            return false; // a notification or an unrelated message
        if (message.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"MCP error: {err.GetRawText()}");
        result = message.GetProperty("result").Clone();   // Clone survives JsonDocument disposal
        return true;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
