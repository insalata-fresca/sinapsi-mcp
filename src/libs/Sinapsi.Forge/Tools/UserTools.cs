using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common user/identity tools. The host injects the concrete <see cref="IForgeClient"/>.
/// Every parameter that reaches a forge URL segment is validated at the top of the tool via
/// <see cref="SinapsiForgeValidation"/>; a failure short-circuits with <c>{ ok:false, error }</c>
/// before any HTTP call, and any upstream error is scrubbed by <see cref="ForgeToolGuard"/>.</summary>
[McpServerToolType]
public sealed class UserTools
{
    [McpServerTool(Name = "get_me", ReadOnly = true)]
    [Description("Get the authenticated user (the token's account).")]
    public static Task<object> GetMe(IForgeClient forge, CancellationToken ct)
        => ForgeToolGuard.RunAsync(async () => await forge.GetMeAsync(ct));

    [McpServerTool(Name = "get_user", ReadOnly = true)]
    [Description("Get a user by login/username.")]
    public static Task<object> GetUser(
        IForgeClient forge,
        [Description("Username / login.")] string username,
        CancellationToken ct)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateSegment(username, "username"),
            async () => await forge.GetUserAsync(username, ct));

    [McpServerTool(Name = "search_users", ReadOnly = true)]
    [Description("Search users by keyword.")]
    public static Task<object> SearchUsers(
        IForgeClient forge,
        [Description("Search query.")] string query,
        [Description("Max results (default 30).")] int limit = 30,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateQuery(query) ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.SearchUsersAsync(query, limit, ct));
}
