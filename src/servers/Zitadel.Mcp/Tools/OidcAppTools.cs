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
        [Description("The ZITADEL project id.")] string project_id,
        [Description("Max results (default 100).")] int limit = 100,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("project_id", project_id) is { } err)
            return Task.FromResult<object>(new { ok = false, error = err });
        if (ZitadelValidation.ValidateLimit(limit) is { } limErr)
            return Task.FromResult<object>(new { ok = false, error = limErr });
        return ZitadelToolGuard.RunAsync(async () => await zitadel.ListAppsAsync(project_id, limit, ct));
    }

    [McpServerTool(Name = "get_oidc_app", ReadOnly = true)]
    [Description("Get a single application within a project.")]
    public static Task<object> GetOidcApp(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string project_id,
        [Description("The application id.")] string app_id,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("project_id", project_id) is { } err)
            return Task.FromResult<object>(new { ok = false, error = err });
        if (ZitadelValidation.ValidateId("app_id", app_id) is { } appErr)
            return Task.FromResult<object>(new { ok = false, error = appErr });
        return ZitadelToolGuard.RunAsync(async () => await zitadel.GetAppAsync(project_id, app_id, ct));
    }

    [McpServerTool(Name = "create_oidc_app", Destructive = false)]
    [Description("Create an OIDC application (= client). Returns {appId, clientId, clientSecret?, details}. MUTATES.")]
    public static Task<object> CreateOidcApp(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string project_id,
        [Description("Application name.")] string name,
        [Description("Redirect URIs (required).")] string[] redirect_uris,
        [Description("Post-logout redirect URIs (default empty).")] string[]? post_logout_redirect_uris = null,
        [Description("Response types (default [\"OIDC_RESPONSE_TYPE_CODE\"]).")] string[]? response_types = null,
        [Description("Grant types (default authorization_code + refresh_token).")] string[]? grant_types = null,
        [Description("App type (default OIDC_APP_TYPE_WEB).")] string app_type = "OIDC_APP_TYPE_WEB",
        [Description("Auth method (default OIDC_AUTH_METHOD_TYPE_BASIC).")] string auth_method_type = "OIDC_AUTH_METHOD_TYPE_BASIC",
        [Description("Access token type (default OIDC_TOKEN_TYPE_BEARER).")] string access_token_type = "OIDC_TOKEN_TYPE_BEARER",
        [Description("Dev mode (default false).")] bool dev_mode = false,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("project_id", project_id) is { } pErr)
            return Task.FromResult<object>(new { ok = false, error = pErr });
        if (ZitadelValidation.ValidateName("name", name) is { } nErr)
            return Task.FromResult<object>(new { ok = false, error = nErr });
        if (ZitadelValidation.ValidateUris("redirect_uris", redirect_uris, required: true) is { } rErr)
            return Task.FromResult<object>(new { ok = false, error = rErr });
        if (ZitadelValidation.ValidateUris("post_logout_redirect_uris", post_logout_redirect_uris, required: false) is { } plErr)
            return Task.FromResult<object>(new { ok = false, error = plErr });
        if (ZitadelValidation.ValidateEnumList("response_types", response_types) is { } rtErr)
            return Task.FromResult<object>(new { ok = false, error = rtErr });
        if (ZitadelValidation.ValidateEnumList("grant_types", grant_types) is { } gtErr)
            return Task.FromResult<object>(new { ok = false, error = gtErr });
        if (ZitadelValidation.ValidateEnum("app_type", app_type) is { } atErr)
            return Task.FromResult<object>(new { ok = false, error = atErr });
        if (ZitadelValidation.ValidateEnum("auth_method_type", auth_method_type) is { } amErr)
            return Task.FromResult<object>(new { ok = false, error = amErr });
        if (ZitadelValidation.ValidateEnum("access_token_type", access_token_type) is { } acErr)
            return Task.FromResult<object>(new { ok = false, error = acErr });

        var body = new
        {
            name,
            redirectUris           = redirect_uris,
            postLogoutRedirectUris = post_logout_redirect_uris ?? Array.Empty<string>(),
            responseTypes          = response_types            ?? new[] { "OIDC_RESPONSE_TYPE_CODE" },
            grantTypes             = grant_types               ?? new[] { "OIDC_GRANT_TYPE_AUTHORIZATION_CODE", "OIDC_GRANT_TYPE_REFRESH_TOKEN" },
            appType                = app_type,
            authMethodType         = auth_method_type,
            accessTokenType        = access_token_type,
            devMode                = dev_mode,
        };
        return ZitadelToolGuard.RunAsync(async () => await zitadel.CreateOidcAppAsync(project_id, body, ct));
    }

    [McpServerTool(Name = "update_oidc_app_config", Destructive = true)]
    [Description("Update an OIDC app's config. Only provided fields are sent (a field omitted is left unchanged). MUTATES.")]
    public static Task<object> UpdateOidcAppConfig(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string project_id,
        [Description("The application id.")] string app_id,
        [Description("Redirect URIs (omit to leave unchanged).")] string[]? redirect_uris = null,
        [Description("Post-logout redirect URIs (omit to leave unchanged).")] string[]? post_logout_redirect_uris = null,
        [Description("Response types (omit to leave unchanged).")] string[]? response_types = null,
        [Description("Grant types (omit to leave unchanged).")] string[]? grant_types = null,
        [Description("App type (omit to leave unchanged).")] string? app_type = null,
        [Description("Auth method (omit to leave unchanged).")] string? auth_method_type = null,
        [Description("Access token type (omit to leave unchanged).")] string? access_token_type = null,
        [Description("Dev mode (omit to leave unchanged; false sets it false).")] bool? dev_mode = null,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("project_id", project_id) is { } pErr)
            return Task.FromResult<object>(new { ok = false, error = pErr });
        if (ZitadelValidation.ValidateId("app_id", app_id) is { } aErr)
            return Task.FromResult<object>(new { ok = false, error = aErr });
        if (ZitadelValidation.ValidateUris("redirect_uris", redirect_uris, required: false) is { } rErr)
            return Task.FromResult<object>(new { ok = false, error = rErr });
        if (ZitadelValidation.ValidateUris("post_logout_redirect_uris", post_logout_redirect_uris, required: false) is { } plErr)
            return Task.FromResult<object>(new { ok = false, error = plErr });
        if (ZitadelValidation.ValidateEnumList("response_types", response_types) is { } rtErr)
            return Task.FromResult<object>(new { ok = false, error = rtErr });
        if (ZitadelValidation.ValidateEnumList("grant_types", grant_types) is { } gtErr)
            return Task.FromResult<object>(new { ok = false, error = gtErr });
        if (ZitadelValidation.ValidateEnum("app_type", app_type) is { } atErr)
            return Task.FromResult<object>(new { ok = false, error = atErr });
        if (ZitadelValidation.ValidateEnum("auth_method_type", auth_method_type) is { } amErr)
            return Task.FromResult<object>(new { ok = false, error = amErr });
        if (ZitadelValidation.ValidateEnum("access_token_type", access_token_type) is { } acErr)
            return Task.FromResult<object>(new { ok = false, error = acErr });

        // Only-non-null body: a bool? null means "leave unchanged"; bool? false means "set false".
        // Dictionary KEYS remain the ZITADEL API's camelCase field names.
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (redirect_uris             is not null) body["redirectUris"]           = redirect_uris;
        if (post_logout_redirect_uris is not null) body["postLogoutRedirectUris"] = post_logout_redirect_uris;
        if (response_types            is not null) body["responseTypes"]          = response_types;
        if (grant_types               is not null) body["grantTypes"]             = grant_types;
        if (app_type                  is not null) body["appType"]                = app_type;
        if (auth_method_type          is not null) body["authMethodType"]         = auth_method_type;
        if (access_token_type         is not null) body["accessTokenType"]        = access_token_type;
        if (dev_mode                  is not null) body["devMode"]                = dev_mode.Value;
        return ZitadelToolGuard.RunAsync(async () => await zitadel.UpdateOidcAppConfigAsync(project_id, app_id, body, ct));
    }

    [McpServerTool(Name = "delete_oidc_app", Destructive = true)]
    [Description("Delete an application by id. MUTATES (irreversible).")]
    public static Task<object> DeleteOidcApp(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string project_id,
        [Description("The application id.")] string app_id,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("project_id", project_id) is { } err)
            return Task.FromResult<object>(new { ok = false, error = err });
        if (ZitadelValidation.ValidateId("app_id", app_id) is { } appErr)
            return Task.FromResult<object>(new { ok = false, error = appErr });
        return ZitadelToolGuard.RunAsync(async () => await zitadel.DeleteAppAsync(project_id, app_id, ct));
    }

    [McpServerTool(Name = "regenerate_oidc_secret", Destructive = true)]
    [Description("Rotate the client secret. Returns the new secret in the response. MUTATES + SENSITIVE — treat the result as a secret.")]
    public static Task<object> RegenerateOidcSecret(
        ZitadelClient zitadel,
        [Description("The ZITADEL project id.")] string project_id,
        [Description("The application id.")] string app_id,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("project_id", project_id) is { } err)
            return Task.FromResult<object>(new { ok = false, error = err });
        if (ZitadelValidation.ValidateId("app_id", app_id) is { } appErr)
            return Task.FromResult<object>(new { ok = false, error = appErr });
        return ZitadelToolGuard.RunAsync(async () => await zitadel.RegenerateOidcSecretAsync(project_id, app_id, ct));
    }
}
