using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// Parity guard: the type must declare exactly the 6 step-ca tools by name, each
/// carrying both an <see cref="McpServerToolAttribute"/> and a description. A
/// dropped or renamed tool fails the build's test gate.
/// </summary>
public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected =
    {
        "get_ca_health", "get_root_certificate", "list_provisioners",
        "issue_certificate", "revoke_certificate", "inspect_certificate",
    };

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods() =>
        typeof(StepCaTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            .Select(x => (x.attr!.Name!, x.m));

    [Fact]
    public void TypeCarriesMcpServerToolTypeAttribute()
    {
        Assert.NotNull(typeof(StepCaTools).GetCustomAttribute<McpServerToolTypeAttribute>());
    }

    [Fact]
    public void ExposesExactlyTheSixTools()
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
