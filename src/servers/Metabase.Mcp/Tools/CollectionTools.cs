using System.ComponentModel;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Read-only Metabase collection tools. The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class CollectionTools
{
    [McpServerTool(Name = "list_collections", ReadOnly = true)]
    [Description("List the collections in the Metabase instance.")]
    public static Task<object> ListCollections(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.ListCollectionsAsync(ct));
}
