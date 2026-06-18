namespace IntentMesh.Integrations;

/// <summary>
/// A transport-agnostic MCP client. The McpProxy gates intent first and forwards through this seam
/// only after IntentMesh approves — so a real server can be reached over stdio
/// (<see cref="McpStdioClient"/>) or Streamable HTTP/SSE (<see cref="McpHttpClient"/>) behind the
/// same gate. "MCP connects tools; IntentMesh verifies intent before tools" — independent of how
/// the bytes travel.
/// </summary>
public interface IMcpClient : IDisposable
{
    /// <summary>tools/list — the names of the tools the server exposes.</summary>
    IReadOnlyList<string> ListTools();

    /// <summary>tools/call — invoke a tool and return the raw JSON result. Only the McpProxy calls
    /// this, and only after IntentMesh has approved the intent.</summary>
    string CallTool(string name, IReadOnlyDictionary<string, string> arguments);
}
