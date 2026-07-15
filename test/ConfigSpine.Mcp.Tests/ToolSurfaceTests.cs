using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace ConfigSpine.Mcp.Tests;

/// <summary>
/// Parity guard: the type must declare exactly the ONE narrow tool (<c>publish_config_event</c>),
/// carrying both an <see cref="McpServerToolAttribute"/> and a description. A dropped, renamed, or
/// silently-added tool fails the build's test gate — this server is deliberately single-purpose.
/// </summary>
public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected = { "publish_config_event" };

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods() =>
        typeof(ConfigSpineTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            .Select(x => (x.attr!.Name ?? x.m.Name, x.m));

    [Fact]
    public void TypeCarriesMcpServerToolTypeAttribute() =>
        Assert.NotNull(typeof(ConfigSpineTools).GetCustomAttribute<McpServerToolTypeAttribute>());

    [Fact]
    public void ExposesExactlyTheOneTool()
    {
        var names = ToolMethods().Select(t => t.name).OrderBy(n => n).ToArray();
        Assert.Equal(Expected.OrderBy(n => n).ToArray(), names);
    }

    [Fact]
    public void TheToolHasADescription()
    {
        foreach (var (name, m) in ToolMethods())
        {
            var desc = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.True(desc is not null && !string.IsNullOrWhiteSpace(desc.Description),
                $"tool '{name}' is missing a [Description]");
        }
    }
}
