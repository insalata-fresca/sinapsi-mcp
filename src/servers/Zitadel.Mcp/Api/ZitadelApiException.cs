namespace Zitadel.Mcp.Api;

/// <summary>
/// A real upstream ZITADEL HTTP failure (non-2xx), carrying the status code and a
/// truncated response body so the cause is legible to the MCP client instead of being
/// swallowed into the SDK's generic "An error occurred invoking 'X'" message.
/// </summary>
public sealed class ZitadelApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
