using Bridge.Mcp.Auth;
using Bridge.Mcp.Tools;
using ModelContextProtocol;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// Tests for defect 2: BridgeToolException must surface as McpException so the MCP SDK
/// includes the error code and message in the isError tool-call result, matching Python
/// FastMCP's ToolError serialisation contract.
///
/// Before the fix: BridgeToolException propagated unhandled → SDK returned a generic
/// "An error occurred invoking '&lt;tool&gt;'." message (code + message both lost).
/// After the fix: BridgeToolError.Throw converts to McpException("{code}: {message}").
/// </summary>
public sealed class ToolErrorContractTests
{
    // ── BridgeToolError.Throw converts to McpException ───────────────────────

    [Fact]
    public void BridgeToolError_Throw_RaisesAsMcpException()
    {
        var inner = new BridgeToolException("not_found", "ste/kb:docs/file.md not found");
        var thrown = Assert.Throws<McpException>(() => BridgeToolError.Throw(inner));
        // The McpException message must be "{code}: {message}" (Python FastMCP parity).
        Assert.Equal("not_found: ste/kb:docs/file.md not found", thrown.Message);
    }

    [Theory]
    [InlineData("not_found",          "some/repo:path not found")]
    [InlineData("invalid_query",      "query must not be empty")]
    [InlineData("insufficient_scope", "Scope bridge:read:documents required")]
    [InlineData("rate_limited",       "read bucket exceeded; retry after 3.0s")]
    [InlineData("missing_bearer",     "No bearer token provided")]
    public void BridgeToolError_Throw_PreservesCodeAndMessage(string code, string message)
    {
        var inner  = new BridgeToolException(code, message);
        var thrown = Assert.Throws<McpException>(() => BridgeToolError.Throw(inner));
        // The McpException.Message must start with the code string exactly.
        Assert.StartsWith(code + ": ", thrown.Message);
        Assert.Contains(message, thrown.Message);
    }

    // ── BridgeToolException code strings are locked to Python values ─────────

    [Theory]
    [InlineData("not_found")]
    [InlineData("invalid_query")]
    [InlineData("insufficient_scope")]
    [InlineData("rate_limited")]
    [InlineData("missing_bearer")]
    public void BridgeToolException_CodeString_IsKnownPythonValue(string code)
    {
        // Ensure the code strings we use are exactly the Python values (no camelCase drift).
        var ex = new BridgeToolException(code, "test");
        Assert.Equal(code, ex.Code);
    }

    // ── BridgeToolError.Throw does not return (DoesNotReturn) ────────────────

    [Fact]
    public void BridgeToolError_Throw_AlwaysThrows()
    {
        // Control flow must never reach after BridgeToolError.Throw.
        var reached = false;
        try
        {
            try
            {
                throw new BridgeToolException("not_found", "x");
            }
            catch (BridgeToolException ex)
            {
                BridgeToolError.Throw(ex);
                // This line must never be reached (DoesNotReturn).
                reached = true;
            }
        }
        catch (McpException) { /* expected */ }
        Assert.False(reached, "BridgeToolError.Throw must never return");
    }

    // ── McpException is the right type (not Exception, not InvalidOperationException) ─

    [Fact]
    public void BridgeToolError_Throw_ThrowsExactlyMcpException()
    {
        var inner = new BridgeToolException("rate_limited", "retry after 5.0s");
        Exception? caught = null;
        try { BridgeToolError.Throw(inner); }
        catch (Exception ex) { caught = ex; }

        Assert.NotNull(caught);
        Assert.IsType<McpException>(caught);
    }
}
