using System.ComponentModel;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Read-only Metabase database tools. The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class DatabaseTools
{
    [McpServerTool(Name = "list_databases", ReadOnly = true)]
    [Description("List the databases configured in the Metabase instance.")]
    public static Task<object> ListDatabases(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.ListDatabasesAsync(ct));
}
