using System.ComponentModel;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Metabase database tools (read + CRUD + sync). The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class DatabaseTools
{
    [McpServerTool(Name = "list_databases", ReadOnly = true)]
    [Description("List the databases configured in the Metabase instance.")]
    public static Task<object> ListDatabases(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.ListDatabasesAsync(ct));

    [McpServerTool(Name = "get_database", ReadOnly = true)]
    [Description("Get one database by id.")]
    public static Task<object> GetDatabase(
        MetabaseClient metabase,
        [Description("Database id.")] int id,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync($"/api/database/{id}", ct));

    [McpServerTool(Name = "get_database_metadata", ReadOnly = true)]
    [Description("Full schema metadata for a database: all tables and their fields (ids, types).")]
    public static Task<object> GetDatabaseMetadata(
        MetabaseClient metabase,
        [Description("Database id.")] int id,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync($"/api/database/{id}/metadata", ct));

    [McpServerTool(Name = "create_database", Destructive = false)]
    [Description("Connect a new database. engine e.g. 'postgres'. details_json is the engine "
        + "connection JSON, e.g. {\"host\":\"127.0.0.1\",\"port\":5432,\"dbname\":\"financials\","
        + "\"user\":\"metabase_ro\",\"password\":\"...\"}. MUTATES.")]
    public static Task<object> CreateDatabase(
        MetabaseClient metabase,
        [Description("Display name.")] string name,
        [Description("Engine, e.g. postgres.")] string engine,
        [Description("Connection details as a JSON string.")] string detailsJson,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync("/api/database", new
        {
            name,
            engine,
            details = MetabaseJson.ParseElement(detailsJson),
            is_full_sync = true,
        }, ct));

    [McpServerTool(Name = "update_database", Destructive = true)]
    [Description("Update a database. patch_json is the partial JSON to merge. MUTATES.")]
    public static Task<object> UpdateDatabase(
        MetabaseClient metabase,
        [Description("Database id.")] int id,
        [Description("Patch as a JSON string.")] string patchJson,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.PutAsync($"/api/database/{id}", MetabaseJson.ParseElement(patchJson), ct));

    [McpServerTool(Name = "delete_database", Destructive = true)]
    [Description("Disconnect/delete a database. MUTATES (destructive).")]
    public static Task<object> DeleteDatabase(
        MetabaseClient metabase,
        [Description("Database id.")] int id,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.DeleteAsync($"/api/database/{id}", ct));

    [McpServerTool(Name = "sync_database_schema", Destructive = false)]
    [Description("Trigger a schema sync (rescan tables/columns) for a database. MUTATES.")]
    public static Task<object> SyncDatabaseSchema(
        MetabaseClient metabase,
        [Description("Database id.")] int id,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync($"/api/database/{id}/sync_schema", new { }, ct));

    [McpServerTool(Name = "rescan_database_values", Destructive = false)]
    [Description("Trigger a re-scan of field values (for filter dropdowns) for a database. MUTATES.")]
    public static Task<object> RescanDatabaseValues(
        MetabaseClient metabase,
        [Description("Database id.")] int id,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync($"/api/database/{id}/rescan_values", new { }, ct));
}
