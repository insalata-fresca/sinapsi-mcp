using System.ComponentModel;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Metabase table + field tools (read + field metadata edit). The host injects the
/// <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class TableTools
{
    [McpServerTool(Name = "list_tables", ReadOnly = true)]
    [Description("List all tables Metabase knows about (across databases).")]
    public static Task<object> ListTables(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync("/api/table", ct));

    [McpServerTool(Name = "get_table_metadata", ReadOnly = true)]
    [Description("Query metadata (fields, types) for one table.")]
    public static Task<object> GetTableMetadata(
        MetabaseClient metabase,
        [Description("Table id.")] int tableId,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync($"/api/table/{tableId}/query_metadata", ct));

    [McpServerTool(Name = "get_field", ReadOnly = true)]
    [Description("Get one field by id.")]
    public static Task<object> GetField(
        MetabaseClient metabase,
        [Description("Field id.")] int fieldId,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync($"/api/field/{fieldId}", ct));

    [McpServerTool(Name = "update_field", Destructive = true)]
    [Description("Update field metadata (display name, semantic type, visibility…). "
        + "patch_json is the partial JSON. MUTATES.")]
    public static Task<object> UpdateField(
        MetabaseClient metabase,
        [Description("Field id.")] int fieldId,
        [Description("Patch as a JSON string.")] string patchJson,
        CancellationToken ct = default)
    {
        if (MetabaseValidation.ValidateRequiredJson("patch_json", patchJson) is { } e) return Task.FromResult(MetabaseToolGuard.Rejected(e));
        return MetabaseToolGuard.RunAsync(async () => await metabase.PutAsync($"/api/field/{fieldId}", MetabaseJson.ParseElement(patchJson), ct));
    }
}
