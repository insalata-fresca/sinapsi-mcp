// ---------------------------------------------------------------------------
// ToolParityTests — the tool-surface parity guard (mirrors StepCa.Mcp's
// ToolSurfaceTests). The gemini surface is spread across several tool classes, so
// this reflects over every [McpServerToolType] in the assembly and asserts the
// exact 10 tool names are declared, each carrying a [Description]. A dropped or
// renamed tool fails the build's test gate — the parity contract this hardening
// pass must not break.
// ---------------------------------------------------------------------------
using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace Gemini.Mcp.Tests;

public sealed class ToolParityTests
{
    private static readonly string[] Expected =
    {
        "ask", "ask_with_files", "research", "sandbox", "image_describe",
        "image_generate", "session_create", "session_resume", "session_close",
        "get_status",
    };

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods() =>
        typeof(AskTool).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            .Select(x => (x.attr!.Name!, x.m));

    [Fact]
    public void Exposes_exactly_the_ten_tools()
    {
        var names = ToolMethods().Select(t => t.name).OrderBy(n => n).ToArray();
        Assert.Equal(Expected.OrderBy(n => n).ToArray(), names);
    }

    [Fact]
    public void Every_tool_has_a_description()
    {
        foreach (var (name, m) in ToolMethods())
        {
            var desc = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.True(desc is not null && !string.IsNullOrWhiteSpace(desc.Description),
                $"tool '{name}' is missing a [Description]");
        }
    }

    [Fact]
    public void No_duplicate_tool_names()
    {
        var names = ToolMethods().Select(t => t.name).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
    }
}
