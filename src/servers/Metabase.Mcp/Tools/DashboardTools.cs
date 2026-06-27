using System.ComponentModel;
using System.Text.Json.Nodes;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Metabase dashboard tools (read + CRUD + a convenience card-placement helper).
/// The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class DashboardTools
{
    [McpServerTool(Name = "list_dashboards", ReadOnly = true)]
    [Description("List dashboards.")]
    public static Task<object> ListDashboards(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync("/api/dashboard", ct));

    [McpServerTool(Name = "get_dashboard", ReadOnly = true)]
    [Description("Get one dashboard by id, including its dashcards (layout).")]
    public static Task<object> GetDashboard(
        MetabaseClient metabase,
        [Description("Dashboard id.")] int id,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync($"/api/dashboard/{id}", ct));

    [McpServerTool(Name = "create_dashboard", Destructive = false)]
    [Description("Create an empty dashboard. Returns it incl. id. MUTATES.")]
    public static Task<object> CreateDashboard(
        MetabaseClient metabase,
        [Description("Dashboard name.")] string name,
        [Description("Optional description.")] string? description = null,
        [Description("Optional collection id.")] int? collectionId = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject { ["name"] = name };
        if (description is not null) body["description"] = description;
        if (collectionId is { } cid) body["collection_id"] = cid;
        return MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync("/api/dashboard", body, ct));
    }

    [McpServerTool(Name = "update_dashboard", Destructive = true)]
    [Description("Update a dashboard. patch_json is the partial JSON. To set the card layout, "
        + "pass {\"dashcards\":[{\"id\":-1,\"card_id\":N,\"row\":0,\"col\":0,\"size_x\":12,"
        + "\"size_y\":6}, …]} (new dashcards use negative ids). MUTATES.")]
    public static Task<object> UpdateDashboard(
        MetabaseClient metabase,
        [Description("Dashboard id.")] int id,
        [Description("Patch as a JSON string.")] string patchJson,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.PutAsync($"/api/dashboard/{id}", MetabaseJson.ParseElement(patchJson), ct));

    [McpServerTool(Name = "delete_dashboard", Destructive = true)]
    [Description("Delete a dashboard. MUTATES (destructive).")]
    public static Task<object> DeleteDashboard(
        MetabaseClient metabase,
        [Description("Dashboard id.")] int id,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.DeleteAsync($"/api/dashboard/{id}", ct));

    [McpServerTool(Name = "add_card_to_dashboard", Destructive = false)]
    [Description("Convenience: append an existing card to a dashboard at a grid position "
        + "(24-col grid). Fetches the dashboard, adds one dashcard, and saves. MUTATES.")]
    public static Task<object> AddCardToDashboard(
        MetabaseClient metabase,
        [Description("Dashboard id.")] int dashboardId,
        [Description("Card id to add.")] int cardId,
        [Description("Grid row (0-based).")] int row = 0,
        [Description("Grid column (0-23).")] int col = 0,
        [Description("Width in grid cols (default 12).")] int sizeX = 12,
        [Description("Height in grid rows (default 6).")] int sizeY = 6,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () =>
        {
            var dashEl = await metabase.GetAsync($"/api/dashboard/{dashboardId}", ct).ConfigureAwait(false);
            var dash = JsonNode.Parse(dashEl.GetRawText())!.AsObject();
            var cards = dash["dashcards"] as JsonArray ?? new JsonArray();
            // new dashcards need a unique negative id
            var minId = 0;
            foreach (var dc in cards)
                if (dc?["id"]?.GetValue<int>() is { } existing && existing < minId) minId = existing;
            var newCard = new JsonObject
            {
                ["id"] = minId - 1,
                ["card_id"] = cardId,
                ["row"] = row,
                ["col"] = col,
                ["size_x"] = sizeX,
                ["size_y"] = sizeY,
                ["series"] = new JsonArray(),
                ["parameter_mappings"] = new JsonArray(),
                ["visualization_settings"] = new JsonObject(),
            };
            var newArr = new JsonArray();
            foreach (var dc in cards) newArr.Add(dc?.DeepClone());
            newArr.Add(newCard);
            return await metabase.PutAsync($"/api/dashboard/{dashboardId}",
                new JsonObject { ["dashcards"] = newArr }, ct).ConfigureAwait(false);
        });
}
