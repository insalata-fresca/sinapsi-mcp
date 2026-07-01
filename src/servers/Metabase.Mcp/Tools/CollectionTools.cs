using System.ComponentModel;
using System.Text.Json.Nodes;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Metabase collection tools (read + create/update). The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class CollectionTools
{
    [McpServerTool(Name = "list_collections", ReadOnly = true)]
    [Description("List the collections in the Metabase instance.")]
    public static Task<object> ListCollections(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.ListCollectionsAsync(ct));

    [McpServerTool(Name = "get_collection_items", ReadOnly = true)]
    [Description("List the items (cards, dashboards, sub-collections) inside a collection. "
        + "Use 'root' for the root collection.")]
    public static Task<object> GetCollectionItems(
        MetabaseClient metabase,
        [Description("Collection id, or 'root'.")] string id,
        CancellationToken ct = default)
    {
        if (MetabaseValidation.ValidateStringId("id", id) is { } e) return Task.FromResult(MetabaseToolGuard.Rejected(e));
        return MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync($"/api/collection/{Uri.EscapeDataString(id)}/items", ct));
    }

    [McpServerTool(Name = "create_collection", Destructive = false)]
    [Description("Create a collection. MUTATES.")]
    public static Task<object> CreateCollection(
        MetabaseClient metabase,
        [Description("Name.")] string name,
        [Description("Optional parent collection id.")] int? parentId = null,
        [Description("Optional description.")] string? description = null,
        CancellationToken ct = default)
    {
        if (MetabaseValidation.ValidateName("name", name) is { } e1) return Task.FromResult(MetabaseToolGuard.Rejected(e1));
        if (MetabaseValidation.ValidateDescription(description) is { } e2) return Task.FromResult(MetabaseToolGuard.Rejected(e2));
        var body = new JsonObject { ["name"] = name };
        if (parentId is { } pid) body["parent_id"] = pid;
        if (description is not null) body["description"] = description;
        return MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync("/api/collection", body, ct));
    }

    [McpServerTool(Name = "update_collection", Destructive = true)]
    [Description("Update a collection. patch_json is the partial JSON. MUTATES.")]
    public static Task<object> UpdateCollection(
        MetabaseClient metabase,
        [Description("Collection id.")] int id,
        [Description("Patch as a JSON string.")] string patchJson,
        CancellationToken ct = default)
    {
        if (MetabaseValidation.ValidateRequiredJson("patch_json", patchJson) is { } e) return Task.FromResult(MetabaseToolGuard.Rejected(e));
        return MetabaseToolGuard.RunAsync(async () => await metabase.PutAsync($"/api/collection/{id}", MetabaseJson.ParseElement(patchJson), ct));
    }
}
