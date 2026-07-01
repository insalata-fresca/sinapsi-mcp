using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Parity guard for the tool surface: the type must declare exactly the four
/// gateway tools by name, each carrying both an <see cref="McpServerToolAttribute"/>
/// and a description. A dropped or renamed tool fails the test gate.
///
/// <para>
/// The schema of <c>execute-command</c> is pinned here to the incumbent contract:
/// it accepts <c>cmdString</c>, an OPTIONAL <c>connectionName</c> (default
/// "default"), an optional <c>directory</c> (working dir), and an optional
/// <c>timeout</c> (per-call ms). <c>upload</c> is a real (elevated write) tool now —
/// no longer a stub — so it takes the registry + transport plus the three path
/// params.
/// </para>
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

    private static MethodInfo Tool(string name) => ToolMethods().Single(t => t.name == name).m;

    private static ParameterInfo Param(string tool, string param) =>
        Tool(tool).GetParameters().Single(p => p.Name == param);

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

    // ── execute-command parity schema ────────────────────────────────────────

    [Fact]
    public void ExecuteCommand_Exposes_cmdString_connectionName_directory_timeout()
    {
        var names = Tool("execute-command").GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("cmdString", names);
        Assert.Contains("connectionName", names);
        Assert.Contains("directory", names);
        Assert.Contains("timeout", names);
    }

    [Fact]
    public void ExecuteCommand_connectionName_IsOptional_DefaultsTo_default()
    {
        var p = Param("execute-command", "connectionName");
        Assert.True(p.HasDefaultValue);
        Assert.Equal("default", p.DefaultValue);
    }

    [Fact]
    public void ExecuteCommand_directory_and_timeout_AreOptional()
    {
        Assert.True(Param("execute-command", "directory").IsOptional);
        Assert.True(Param("execute-command", "timeout").IsOptional);
        // timeout is a nullable int (per-call override; null ⇒ configured default).
        Assert.Equal(typeof(int?), Param("execute-command", "timeout").ParameterType);
    }

    // ── upload is a real tool (not a stub) ───────────────────────────────────

    [Fact]
    public void Upload_IsImplemented_TakesRegistryAndTransport_And_connectionName_IsOptional()
    {
        var m = Tool("upload");
        var pnames = m.GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("localPath", pnames);
        Assert.Contains("remotePath", pnames);
        Assert.Contains("connectionName", pnames);
        // Injected dependencies prove it is wired to the real transport, not a stub.
        Assert.Contains(m.GetParameters(), p => p.ParameterType == typeof(ServerRegistry));
        Assert.Contains(m.GetParameters(), p => p.ParameterType == typeof(SshClient));
        Assert.Equal("default", Param("upload", "connectionName").DefaultValue);
    }
}
