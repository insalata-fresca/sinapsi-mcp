using System.ComponentModel;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>Read-only Metabase user tools. The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class UserTools
{
    [McpServerTool(Name = "current_user", ReadOnly = true)]
    [Description("Identity of the API key this MCP authenticates as.")]
    public static Task<object> CurrentUser(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync("/api/user/current", ct));

    [McpServerTool(Name = "list_users", ReadOnly = true)]
    [Description("List Metabase users.")]
    public static Task<object> ListUsers(
        MetabaseClient metabase,
        CancellationToken ct = default)
        => MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync("/api/user", ct));
}
