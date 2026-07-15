using System.Reflection;
using ApprovalBridge.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace ApprovalBridge.Mcp.Tests;

/// <summary>
/// Parity + request-only guard: <see cref="ApprovalBridgeTools"/> must declare EXACTLY ONE tool —
/// <c>approval_bridge_request</c> — and never an <c>approve</c> / <c>reject</c> tool (CARD
/// in_scope: "the tool must expose ONLY request — never approve/reject", docs/66 §8 T1). A future
/// edit that added an approve/reject MCP tool would flip this test red.
/// </summary>
public sealed class ToolSurfaceTests
{
    private const string ExpectedTool = "approval_bridge_request";

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods() =>
        typeof(ApprovalBridgeTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            .Select(x => (x.attr!.Name!, x.m));

    [Fact]
    public void TypeCarriesMcpServerToolTypeAttribute()
    {
        Assert.NotNull(typeof(ApprovalBridgeTools).GetCustomAttribute<McpServerToolTypeAttribute>());
    }

    [Fact]
    public void ExposesExactlyOneTool_TheRequestTool()
    {
        var names = ToolMethods().Select(t => t.name).ToArray();
        Assert.Single(names);
        Assert.Equal(ExpectedTool, names[0]);
    }

    [Fact]
    public void DoesNotExposeAnApproveOrRejectTool()
    {
        var names = ToolMethods().Select(t => t.name.ToLowerInvariant()).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("approve"));
        Assert.DoesNotContain(names, n => n.Contains("reject"));
    }

    [Fact]
    public void TheRequestToolHasADescription()
    {
        var (name, m) = ToolMethods().Single();
        var desc = m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        Assert.True(desc is not null && !string.IsNullOrWhiteSpace(desc.Description),
            $"tool '{name}' is missing a [Description]");
    }
}
