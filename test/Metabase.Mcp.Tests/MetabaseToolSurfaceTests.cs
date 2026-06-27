using System.Reflection;
using Metabase.Mcp.Api;
using Metabase.Mcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Metabase.Mcp.Tests;

/// <summary>
/// Pins the server's real registered tool surface: every tool the host advertises
/// (the ones <c>Program.cs</c> registers via <c>WithTools&lt;T&gt;()</c>) is present with
/// the expected MCP name and is marked read-only — the host exposes only reads. One
/// assertion per exposed tool's contract.
/// </summary>
public sealed class MetabaseToolSurfaceTests
{
    // The three tool classes Program.cs registers via WithTools<T>().
    private static readonly Type[] RegisteredToolTypes =
        { typeof(DatabaseTools), typeof(CollectionTools), typeof(CardTools) };

    private static (string Name, bool ReadOnly) Tool(Type type, string method)
    {
        var attr = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .GetCustomAttribute<McpServerToolAttribute>()!;
        return (attr.Name!, attr.ReadOnly);
    }

    [Fact]
    public void Every_registered_tool_class_is_an_McpServerToolType()
    {
        foreach (var t in RegisteredToolTypes)
            Assert.NotNull(t.GetCustomAttribute<McpServerToolTypeAttribute>());
    }

    [Theory]
    [InlineData(typeof(DatabaseTools), nameof(DatabaseTools.ListDatabases), "list_databases")]
    [InlineData(typeof(CollectionTools), nameof(CollectionTools.ListCollections), "list_collections")]
    [InlineData(typeof(CardTools), nameof(CardTools.ListCards), "list_cards")]
    [InlineData(typeof(CardTools), nameof(CardTools.GetCard), "get_card")]
    public void Exposed_tool_has_expected_name_and_is_read_only(Type type, string method, string expectedName)
    {
        var (name, readOnly) = Tool(type, method);
        Assert.Equal(expectedName, name);
        Assert.True(readOnly);
    }

    [Fact]
    public void Registered_surface_is_exactly_the_four_expected_read_tools()
    {
        var names = RegisteredToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[] { "get_card", "list_cards", "list_collections", "list_databases" },
            names);
    }

    [Fact]
    public async Task Tool_guard_maps_an_api_failure_to_a_structured_error_payload()
    {
        var result = await MetabaseToolGuard.RunAsync(
            () => throw new MetabaseApiException(404, "404 Not Found: no such card"));

        var ok = (bool)result.GetType().GetProperty("ok")!.GetValue(result)!;
        var status = (int)result.GetType().GetProperty("status")!.GetValue(result)!;
        Assert.False(ok);
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Tool_guard_returns_a_successful_payload_unchanged()
    {
        var payload = new { result = "ok" };
        var result = await MetabaseToolGuard.RunAsync(() => Task.FromResult<object>(payload));
        Assert.Same(payload, result);
    }
}
