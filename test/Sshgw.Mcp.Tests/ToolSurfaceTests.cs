using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Parity guard for the tool surface: the type must declare exactly the four
/// gateway tools by name, each carrying both an <see cref="McpServerToolAttribute"/>
/// and a description. A dropped or renamed tool fails the test gate. The
/// <c>upload</c> tool is a deliberate write stub here (as in the reference
/// implementation); the test pins that contract so the stub cannot silently turn
/// into a half-implementation without the test being updated on purpose.
/// </summary>
public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected =
    {
        "list-servers", "execute-command", "read_file", "upload",
    };

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods() =>
        typeof(SshgwTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            .Select(x => (x.attr!.Name!, x.m));

    [Fact]
    public void TypeCarriesMcpServerToolTypeAttribute()
    {
        Assert.NotNull(typeof(SshgwTools).GetCustomAttribute<McpServerToolTypeAttribute>());
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
            var desc = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.True(desc is not null && !string.IsNullOrWhiteSpace(desc.Description),
                $"tool '{name}' is missing a [Description]");
        }
    }

    [Fact]
    public void Upload_is_a_stub_returning_a_not_implemented_error()
    {
        // The reference implementation keeps upload as a deliberate stub; the port
        // must too. It returns an error envelope, never silently "succeeds".
        var result = SshgwTools.Upload("any-server", "/local/path", "/remote/path");
        Assert.False(result["ok"]!.GetValue<bool>());
        var err = result["error"]!.GetValue<string>();
        Assert.Contains("upload", err, System.StringComparison.OrdinalIgnoreCase);
    }
}
