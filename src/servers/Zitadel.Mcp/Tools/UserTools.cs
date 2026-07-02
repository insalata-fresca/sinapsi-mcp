using System.ComponentModel;
using ModelContextProtocol.Server;
using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Tools;

/// <summary>Read-only ZITADEL user/identity tools. The host injects the <see cref="ZitadelClient"/>.</summary>
[McpServerToolType]
public sealed class UserTools
{
    [McpServerTool(Name = "list_users", ReadOnly = true)]
    [Description("List users in the ZITADEL instance (first page).")]
    public static Task<object> ListUsers(
        ZitadelClient zitadel,
        [Description("Max results (default 100).")] int limit = 100,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateLimit(limit) is { } err)
            return Task.FromResult<object>(new { ok = false, error = err });
        return ZitadelToolGuard.RunAsync(async () => await zitadel.ListUsersAsync(limit, ct));
    }

    [McpServerTool(Name = "get_user", ReadOnly = true)]
    [Description("Get a single user by id.")]
    public static Task<object> GetUser(
        ZitadelClient zitadel,
        [Description("The ZITADEL user id.")] string user_id,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("user_id", user_id) is { } err)
            return Task.FromResult<object>(new { ok = false, error = err });
        return ZitadelToolGuard.RunAsync(async () => await zitadel.GetUserAsync(user_id, ct));
    }
}
