using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common user/identity tools. The host injects the concrete <see cref="IForgeClient"/>.</summary>
[McpServerToolType]
public sealed class UserTools
{
    [McpServerTool(Name = "get_me", ReadOnly = true)]
    [Description("Get the authenticated user (the token's account).")]
    public static async Task<object> GetMe(IForgeClient forge, CancellationToken ct)
        => await forge.GetMeAsync(ct);

    [McpServerTool(Name = "get_user", ReadOnly = true)]
    [Description("Get a user by login/username.")]
    public static async Task<object> GetUser(
        IForgeClient forge,
        [Description("Username / login.")] string username,
        CancellationToken ct)
        => await forge.GetUserAsync(username, ct);

    [McpServerTool(Name = "search_users", ReadOnly = true)]
    [Description("Search users by keyword.")]
    public static async Task<object> SearchUsers(
        IForgeClient forge,
        [Description("Search query.")] string query,
        [Description("Max results (default 30).")] int limit = 30,
        CancellationToken ct = default)
        => await forge.SearchUsersAsync(query, limit, ct);
}
