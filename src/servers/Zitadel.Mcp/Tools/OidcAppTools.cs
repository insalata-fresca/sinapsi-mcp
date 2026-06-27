using System.ComponentModel;
using ModelContextProtocol.Server;
using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Tools;

/// <summary>Read-only ZITADEL OIDC-application tools. The host injects the <see cref="ZitadelClient"/>.</summary>
[McpServerToolType]
public sealed class OidcAppTools
{
    [McpServerTool(Name = "list_oidc_apps", ReadOnly = true)]
    [Description("List the applications registered under a project (first page).")]
    public static Task<object> ListOidcApps(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string projectId,
        [Description("Max results (default 100).")] int limit = 100,
        CancellationToken ct = default)
        => ZitadelToolGuard.RunAsync(async () => await zitadel.ListAppsAsync(projectId, limit, ct));

    [McpServerTool(Name = "get_oidc_app", ReadOnly = true)]
    [Description("Get a single application within a project.")]
    public static Task<object> GetOidcApp(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string projectId,
        [Description("The application id.")] string appId,
        CancellationToken ct = default)
        => ZitadelToolGuard.RunAsync(async () => await zitadel.GetAppAsync(projectId, appId, ct));
}
