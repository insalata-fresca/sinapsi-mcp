using System.ComponentModel;
using ModelContextProtocol.Server;
using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Tools;

/// <summary>ZITADEL OIDC-application tools (read + CRUD + secret rotation). The host injects
/// the <see cref="ZitadelClient"/>.</summary>
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

    [McpServerTool(Name = "create_oidc_app", Destructive = false)]
    [Description("Create an OIDC application (= client). Returns {appId, clientId, clientSecret?, details}. MUTATES.")]
    public static Task<object> CreateOidcApp(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string projectId,
        [Description("Application name.")] string name,
        [Description("Redirect URIs (required).")] string[] redirectUris,
        [Description("Post-logout redirect URIs (default empty).")] string[]? postLogoutRedirectUris = null,
        [Description("Response types (default [\"OIDC_RESPONSE_TYPE_CODE\"]).")] string[]? responseTypes = null,
        [Description("Grant types (default authorization_code + refresh_token).")] string[]? grantTypes = null,
        [Description("App type (default OIDC_APP_TYPE_WEB).")] string appType = "OIDC_APP_TYPE_WEB",
        [Description("Auth method (default OIDC_AUTH_METHOD_TYPE_BASIC).")] string authMethodType = "OIDC_AUTH_METHOD_TYPE_BASIC",
        [Description("Access token type (default OIDC_TOKEN_TYPE_BEARER).")] string accessTokenType = "OIDC_TOKEN_TYPE_BEARER",
        [Description("Dev mode (default false).")] bool devMode = false,
        CancellationToken ct = default)
    {
        var body = new
        {
            name,
            redirectUris,
            postLogoutRedirectUris = postLogoutRedirectUris ?? Array.Empty<string>(),
            responseTypes          = responseTypes          ?? new[] { "OIDC_RESPONSE_TYPE_CODE" },
            grantTypes             = grantTypes             ?? new[] { "OIDC_GRANT_TYPE_AUTHORIZATION_CODE", "OIDC_GRANT_TYPE_REFRESH_TOKEN" },
            appType,
            authMethodType,
            accessTokenType,
            devMode,
        };
        return ZitadelToolGuard.RunAsync(async () => await zitadel.CreateOidcAppAsync(projectId, body, ct));
    }

    [McpServerTool(Name = "update_oidc_app_config", Destructive = true)]
    [Description("Update an OIDC app's config. Only provided fields are sent (a field omitted is left unchanged). MUTATES.")]
    public static Task<object> UpdateOidcAppConfig(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string projectId,
        [Description("The application id.")] string appId,
        [Description("Redirect URIs (omit to leave unchanged).")] string[]? redirectUris = null,
        [Description("Post-logout redirect URIs (omit to leave unchanged).")] string[]? postLogoutRedirectUris = null,
        [Description("Response types (omit to leave unchanged).")] string[]? responseTypes = null,
        [Description("Grant types (omit to leave unchanged).")] string[]? grantTypes = null,
        [Description("App type (omit to leave unchanged).")] string? appType = null,
        [Description("Auth method (omit to leave unchanged).")] string? authMethodType = null,
        [Description("Access token type (omit to leave unchanged).")] string? accessTokenType = null,
        [Description("Dev mode (omit to leave unchanged; false sets it false).")] bool? devMode = null,
        CancellationToken ct = default)
    {
        // Only-non-null body: a bool? null means "leave unchanged"; bool? false means "set false".
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (redirectUris           is not null) body["redirectUris"]           = redirectUris;
        if (postLogoutRedirectUris is not null) body["postLogoutRedirectUris"] = postLogoutRedirectUris;
        if (responseTypes          is not null) body["responseTypes"]          = responseTypes;
        if (grantTypes             is not null) body["grantTypes"]             = grantTypes;
        if (appType                is not null) body["appType"]                = appType;
        if (authMethodType         is not null) body["authMethodType"]         = authMethodType;
        if (accessTokenType        is not null) body["accessTokenType"]        = accessTokenType;
        if (devMode                is not null) body["devMode"]                = devMode.Value;
        return ZitadelToolGuard.RunAsync(async () => await zitadel.UpdateOidcAppConfigAsync(projectId, appId, body, ct));
    }

    [McpServerTool(Name = "delete_oidc_app", Destructive = true)]
    [Description("Delete an application by id. MUTATES (irreversible).")]
    public static Task<object> DeleteOidcApp(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string projectId,
        [Description("The application id.")] string appId,
        CancellationToken ct = default)
        => ZitadelToolGuard.RunAsync(async () => await zitadel.DeleteAppAsync(projectId, appId, ct));

    [McpServerTool(Name = "regenerate_oidc_secret", Destructive = true)]
    [Description("Rotate the client secret. Returns the new secret in the response. MUTATES + SENSITIVE — treat the result as a secret.")]
    public static Task<object> RegenerateOidcSecret(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string projectId,
        [Description("The application id.")] string appId,
        CancellationToken ct = default)
        => ZitadelToolGuard.RunAsync(async () => await zitadel.RegenerateOidcSecretAsync(projectId, appId, ct));
}
