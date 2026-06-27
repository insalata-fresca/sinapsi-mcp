using System.ComponentModel;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Read-only Metabase saved-question (card) tools. The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class CardTools
{
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
}
