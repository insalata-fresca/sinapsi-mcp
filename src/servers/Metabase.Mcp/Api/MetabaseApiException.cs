namespace Metabase.Mcp.Api;

/// <summary>
/// A real upstream Metabase HTTP failure (non-2xx), carrying the status code and a
/// truncated response body so the cause is legible to the MCP client instead of being
/// swallowed into the SDK's generic "An error occurred invoking 'X'" message.
/// </summary>
public sealed class MetabaseApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
