using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace SageCouncil.Mcp.Tests;

// -----------------------------------------------------------------------------
// Parity guard: the type must declare EXACTLY the two council tools by name
// (`consult` + `consult_result` — an async job-dispatch + poll pair), each
// carrying both an McpServerToolAttribute and a [Description]. A dropped or
// renamed tool fails the build's test gate, so the hardening pass cannot silently
// change the tool surface.
// -----------------------------------------------------------------------------

public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected = { "consult", "consult_result" };

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods() =>
        typeof(ConsultTool)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            .Select(x => (x.attr!.Name!, x.m));

    [Fact]
    public void TypeCarriesMcpServerToolTypeAttribute()
    {
        Assert.NotNull(typeof(ConsultTool).GetCustomAttribute<McpServerToolTypeAttribute>());
    }

    [Fact]
    public void ExposesExactlyTheTwoTools()
    {
        var names = ToolMethods().Select(t => t.name).OrderBy(n => n).ToArray();
        Assert.Equal(Expected.OrderBy(n => n).ToArray(), names);
    }

    [Fact]
    public void EveryToolHasADescription()
    {
        foreach (var (name, m) in ToolMethods())
        {
            var desc = m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            Assert.True(desc is not null && !string.IsNullOrWhiteSpace(desc.Description),
                $"tool '{name}' is missing a [Description]");
        }
    }
}
