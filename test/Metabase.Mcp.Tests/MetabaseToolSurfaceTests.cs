using System.Reflection;
using Metabase.Mcp.Api;
using Metabase.Mcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Metabase.Mcp.Tests;

/// <summary>
/// Pins the server's real registered tool surface: every tool the host advertises
/// (the ones <c>Program.cs</c> registers via <c>WithTools&lt;T&gt;()</c>) is present with
/// the expected MCP name and its read/mutate classification. The surface is the full
/// 34-tool Metabase surface — the catalog reads plus the query capability
/// (run_native_query / run_card_query), the generic <c>request</c> escape hatch, <c>search</c>,
/// and the database / table / field / card / dashboard / collection / user CRUD.
/// </summary>
public sealed class MetabaseToolSurfaceTests
{
    // The seven tool classes Program.cs registers via WithTools<T>().
    private static readonly Type[] RegisteredToolTypes =
    {
        typeof(DatabaseTools), typeof(TableTools), typeof(CollectionTools), typeof(CardTools),
        typeof(DashboardTools), typeof(UserTools), typeof(ApiTools),
    };

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
    // reads
    [InlineData(typeof(DatabaseTools), nameof(DatabaseTools.ListDatabases), "list_databases", true)]
    [InlineData(typeof(CollectionTools), nameof(CollectionTools.ListCollections), "list_collections", true)]
    [InlineData(typeof(CardTools), nameof(CardTools.ListCards), "list_cards", true)]
    [InlineData(typeof(CardTools), nameof(CardTools.GetCard), "get_card", true)]
    [InlineData(typeof(CardTools), nameof(CardTools.RunNativeQuery), "run_native_query", true)]
    [InlineData(typeof(CardTools), nameof(CardTools.RunCardQuery), "run_card_query", true)]
    [InlineData(typeof(TableTools), nameof(TableTools.ListTables), "list_tables", true)]
    [InlineData(typeof(ApiTools), nameof(ApiTools.Search), "search", true)]
    [InlineData(typeof(UserTools), nameof(UserTools.CurrentUser), "current_user", true)]
    // mutations
    [InlineData(typeof(CardTools), nameof(CardTools.CreateNativeCard), "create_native_card", false)]
    [InlineData(typeof(CardTools), nameof(CardTools.DeleteCard), "delete_card", false)]
    [InlineData(typeof(DatabaseTools), nameof(DatabaseTools.CreateDatabase), "create_database", false)]
    [InlineData(typeof(DashboardTools), nameof(DashboardTools.CreateDashboard), "create_dashboard", false)]
    [InlineData(typeof(ApiTools), nameof(ApiTools.Request), "request", false)]
    public void Exposed_tool_has_expected_name_and_classification(Type type, string method, string expectedName, bool readOnly)
    {
        var (name, isReadOnly) = Tool(type, method);
        Assert.Equal(expectedName, name);
        Assert.Equal(readOnly, isReadOnly);
    }

    [Fact]
    public void Registered_surface_is_exactly_the_thirty_four_expected_tools()
    {
        var names = RegisteredToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "add_card_to_dashboard", "create_card", "create_collection", "create_dashboard",
                "create_database", "create_native_card", "current_user", "delete_card",
                "delete_dashboard", "delete_database", "get_card", "get_collection_items",
                "get_dashboard", "get_database", "get_database_metadata", "get_field",
                "get_table_metadata", "list_cards", "list_collections", "list_dashboards",
                "list_databases", "list_tables", "list_users", "request",
                "rescan_database_values", "run_card_query", "run_native_query", "search",
                "sync_database_schema", "update_card", "update_collection", "update_dashboard",
                "update_database", "update_field",
            },
            names);
    }

    [Fact]
    public void The_query_capability_and_escape_hatch_are_present()
    {
        // run_native_query / run_card_query / search / request are the primary value of a
        // Metabase MCP — they must never be silently dropped from the surface.
        var names = RegisteredToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .ToHashSet();

        Assert.Contains("run_native_query", names);
        Assert.Contains("run_card_query", names);
        Assert.Contains("search", names);
        Assert.Contains("request", names);
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
