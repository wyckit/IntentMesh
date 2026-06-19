namespace IntentMesh.Integrations;

/// <summary>
/// An <see cref="IMcpClient"/> decorator that retries TRANSIENT transport failures (network/IO/
/// timeout) with exponential backoff — for realistic use against flaky servers. It never retries a
/// fatal error: an MCP protocol error surfaces as <see cref="InvalidOperationException"/> and is
/// rethrown immediately, and a PolicyGate Block never reaches a forwarded call in the first place, so
/// retrying can't re-attempt a blocked action. The backoff delay is injectable so the retry loop is
/// unit-testable with no real waiting.
///
/// <para><b>Idempotency:</b> read-only <see cref="ListTools"/> is always retried. <see cref="CallTool"/>
/// is NOT retried by default — a transient failure AFTER the server already performed a
/// send/write/delete would otherwise re-invoke the tool and duplicate the side effect. Enable
/// <c>retryToolCalls</c> only when the tools you forward are idempotent (and ideally pair it with a
/// per-call idempotency key the server honours).</para>
/// </summary>
public sealed class RetryingMcpClient : IMcpClient
{
    private readonly IMcpClient _inner;
    private readonly int _maxAttempts;
    private readonly Func<int, Task> _delay;
    private readonly bool _retryToolCalls;

    public RetryingMcpClient(IMcpClient inner, int maxAttempts = 3, Func<int, Task>? delay = null, bool retryToolCalls = false)
    {
        _inner = inner;
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryToolCalls = retryToolCalls;
        _delay = delay ?? (attempt => Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt))));
    }

    public IReadOnlyList<string> ListTools() => Run(() => _inner.ListTools());

    /// <summary>Forward a tool call. Retried only when <c>retryToolCalls</c> was enabled — by default a
    /// single attempt, so a non-idempotent side effect is never re-issued after a mid-call timeout.</summary>
    public string CallTool(string name, IReadOnlyDictionary<string, string> arguments)
        => _retryToolCalls ? Run(() => _inner.CallTool(name, arguments)) : _inner.CallTool(name, arguments);

    private T Run<T>(Func<T> op)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            try { return op(); }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                if (attempt + 1 < _maxAttempts) _delay(attempt).GetAwaiter().GetResult();
            }
        }
        throw last!;   // attempts exhausted on a transient error
    }

    /// <summary>Transient = network/IO/timeout. An <see cref="InvalidOperationException"/> (MCP
    /// protocol error, HTTP error status, SSRF rejection) is fatal and never retried.</summary>
    private static bool IsTransient(Exception ex)
        => ex is IOException or HttpRequestException or TaskCanceledException or TimeoutException;

    public void Dispose() => _inner.Dispose();
}
