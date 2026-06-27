using System.ComponentModel;
using System.Text.Json.Nodes;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Metabase saved-question (card) tools (read + CRUD) and the query surface
/// (run native SQL / run a saved card). The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class CardTools
{
    // ── reads ──────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_cards", ReadOnly = true)]
    [Description("List the saved questions (cards) in the Metabase instance.")]
    public static Task<object> ListCards(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.ListCardsAsync(ct));

    [McpServerTool(Name = "get_card", ReadOnly = true)]
    [Description("Get a single saved question (card) by id.")]
    public static Task<object> GetCard(
        MetabaseClient metabase,
        [Description("The card id.")] long cardId,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetCardAsync(cardId, ct));

    // ── run queries (the primary value of the MCP) ──────────────────────────────

    [McpServerTool(Name = "run_native_query", ReadOnly = true)]
    [Description("Run an ad-hoc native SQL query against a database and return the rows. "
        + "The best tool to inspect data directly.")]
    public static Task<object> RunNativeQuery(
        MetabaseClient metabase,
        [Description("Database id.")] int databaseId,
        [Description("SQL query text.")] string sql,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync("/api/dataset", new
        {
            database = databaseId,
            type = "native",
            native = new { query = sql },
        }, ct));

    [McpServerTool(Name = "run_card_query", ReadOnly = true)]
    [Description("Run a saved card (question) and return its result rows. "
        + "parameters_json is an optional JSON array of parameter values.")]
    public static Task<object> RunCardQuery(
        MetabaseClient metabase,
        [Description("Card id.")] int cardId,
        [Description("Optional parameters as a JSON array string.")] string? parametersJson = null,
        CancellationToken ct = default)
    {
        object body = string.IsNullOrWhiteSpace(parametersJson)
            ? new { }
            : new { parameters = MetabaseJson.ParseElement(parametersJson) };
        return MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync($"/api/card/{cardId}/query", body, ct));
    }

    // ── card CRUD ───────────────────────────────────────────────────────────────

    [McpServerTool(Name = "create_native_card", Destructive = false)]
    [Description("Create a native-SQL card (question). display e.g. table|bar|line|scalar|pie. "
        + "visualization_settings_json optional. Returns the created card incl. its id. MUTATES.")]
    public static Task<object> CreateNativeCard(
        MetabaseClient metabase,
        [Description("Card name.")] string name,
        [Description("Database id to query.")] int databaseId,
        [Description("SQL query text.")] string sql,
        [Description("Display type, default table.")] string display = "table",
        [Description("Optional visualization_settings JSON string.")] string? visualizationSettingsJson = null,
        [Description("Optional collection id to file it under.")] int? collectionId = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["display"] = display,
            ["dataset_query"] = new JsonObject
            {
                ["database"] = databaseId,
                ["type"] = "native",
                ["native"] = new JsonObject { ["query"] = sql },
            },
            ["visualization_settings"] = MetabaseJson.ParseNode(visualizationSettingsJson) ?? new JsonObject(),
        };
        if (collectionId is { } cid) body["collection_id"] = cid;
        return MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync("/api/card", body, ct));
    }

    [McpServerTool(Name = "create_card", Destructive = false)]
    [Description("Create a card from a full body_json (MBQL or native, full control). MUTATES.")]
    public static Task<object> CreateCard(
        MetabaseClient metabase,
        [Description("Full card body as a JSON string.")] string bodyJson,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.PostAsync("/api/card", MetabaseJson.ParseElement(bodyJson), ct));

    [McpServerTool(Name = "update_card", Destructive = true)]
    [Description("Update a card. patch_json is the partial JSON (e.g. {\"name\":\"x\"} or "
        + "{\"archived\":true} to archive). MUTATES.")]
    public static Task<object> UpdateCard(
        MetabaseClient metabase,
        [Description("Card id.")] int id,
        [Description("Patch as a JSON string.")] string patchJson,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.PutAsync($"/api/card/{id}", MetabaseJson.ParseElement(patchJson), ct));

    [McpServerTool(Name = "delete_card", Destructive = true)]
    [Description("Delete a card permanently. Prefer archiving via update_card {\"archived\":true}. MUTATES (destructive).")]
    public static Task<object> DeleteCard(
        MetabaseClient metabase,
        [Description("Card id.")] int id,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.DeleteAsync($"/api/card/{id}", ct));
}
