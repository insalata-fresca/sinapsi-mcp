using Bridge.Mcp.Auth;
using ModelContextProtocol;

namespace Bridge.Mcp.Tools;

/// <summary>
/// Converts a <see cref="BridgeToolException"/> into an <see cref="McpException"/>
/// so the MCP SDK includes the error code and message in the tool-call error result
/// sent to the client (isError=true, content=[{type:"text",text:"code: message"}]).
///
/// Python (FastMCP) serialises ToolError/AuthError str() into the error content.
/// The Python error-code strings ("not_found", "insufficient_scope", "rate_limited",
/// "invalid_query", "invalid_repo", "missing_bearer") must be preserved verbatim.
///
/// C# without this conversion: BridgeToolException propagates unhandled to the MCP SDK,
/// which returns a generic "An error occurred invoking '&lt;tool&gt;'." message — the code
/// and human message are both lost (S36 equivalence failure).
///
/// Usage in every tool method:
/// <code>
/// try { … }
/// catch (BridgeToolException ex) { BridgeToolError.Throw(ex); }
/// </code>
/// </summary>
internal static class BridgeToolError
{
    /// <summary>
    /// Re-throws <paramref name="ex"/> as <see cref="McpException"/> whose
    /// <see cref="Exception.Message"/> is "{code}: {message}" — the same string
    /// Python's FastMCP surfaces from ToolError(code, message).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void Throw(BridgeToolException ex)
        => throw new McpException($"{ex.Code}: {ex.Message}");
}
