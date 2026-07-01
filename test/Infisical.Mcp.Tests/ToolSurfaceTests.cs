using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace Infisical.Mcp.Tests;

/// <summary>
/// Parity guard: the type must declare exactly the 4 Infisical tools by name, each
/// carrying both an <see cref="McpServerToolAttribute"/> and a description. A dropped or
/// renamed tool fails the build's test gate. (The tool methods are instance methods, so
/// the reflection here includes instance binding.)
/// </summary>
public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected =
    {
        "issue_nats_nkey", "issue_random_secret", "set_secret", "list_secrets",
    };

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods() =>
        typeof(InfisicalTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            // These tools do not set an explicit Name on the attribute, so the tool name
            // is the method name (the MCP SDK default).
            .Select(x => (x.attr!.Name ?? x.m.Name, x.m));

    [Fact]
    public void TypeCarriesMcpServerToolTypeAttribute()
    {
        Assert.NotNull(typeof(InfisicalTools).GetCustomAttribute<McpServerToolTypeAttribute>());
    }

    [Fact]
    public void ExposesExactlyTheFourTools()
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
