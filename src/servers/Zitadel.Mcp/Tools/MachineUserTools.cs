using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Tools;

/// <summary>
/// ZITADEL machine (service) user lifecycle tools — the machine-to-machine (M2M)
/// identity-minting surface: create/update/delete a machine user, issue a Personal Access
/// Token, and issue a JSON private key written host-side (never returned to the caller).
/// The host injects the <see cref="ZitadelClient"/> as the single leading DI param on every
/// tool; <c>create_machine_key</c> reaches AGENT_KEY_DIR through <c>ZitadelClient.AgentKeyDir</c>
/// rather than a second injected parameter (a second DI param interleaved before the tool params
/// breaks the MCP SDK's reflection marshaller).
/// </summary>
[McpServerToolType]
public sealed class MachineUserTools
{
    [McpServerTool(Name = "create_machine_user", Destructive = false)]
    [Description("Create a machine (service) user. Returns {userId, details}. MUTATES. " +
        "access_token_type defaults to ACCESS_TOKEN_TYPE_JWT — a JWT access token is self-validatable " +
        "by a gateway's JWKS validator, whereas a BEARER token is opaque and must be introspected. " +
        "Use ACCESS_TOKEN_TYPE_BEARER only for an introspection-based client.")]
    public static Task<object> CreateMachineUser(
        ZitadelClient zitadel,
        [Description("Username (loginName).")] string username,
        [Description("Display name (default = username).")] string? name = null,
        [Description("Description (default empty).")] string? description = null,
        [Description("Access token type — default ACCESS_TOKEN_TYPE_JWT.")] string access_token_type = "ACCESS_TOKEN_TYPE_JWT",
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateName("username", username) is { } uErr)
            return Task.FromResult<object>(new { ok = false, error = uErr });
        if (ZitadelValidation.ValidateName("name", name ?? username) is { } nErr)
            return Task.FromResult<object>(new { ok = false, error = nErr });
        if (ZitadelValidation.ValidateDescription(description) is { } dErr)
            return Task.FromResult<object>(new { ok = false, error = dErr });
        if (ZitadelValidation.ValidateEnum("access_token_type", access_token_type) is { } aErr)
            return Task.FromResult<object>(new { ok = false, error = aErr });

        var body = new
        {
            userName        = username,
            name            = name ?? username,
            description     = description ?? "",
            accessTokenType = access_token_type,
        };
        return ZitadelToolGuard.RunAsync(async () => await zitadel.CreateMachineUserAsync(body, ct));
    }

    [McpServerTool(Name = "update_machine_user", Destructive = true)]
    [Description("Update a machine (service) user — name, description, and/or access-token type. " +
        "Set access_token_type=ACCESS_TOKEN_TYPE_JWT to make the user's tokens self-validatable by a " +
        "gateway's JWKS validator (a BEARER token is opaque and must be introspected). MUTATES.")]
    public static Task<object> UpdateMachineUser(
        ZitadelClient zitadel,
        [Description("Machine user id.")] string user_id,
        [Description("Display name (ZITADEL's UpdateMachine requires it — pass the existing name to leave it unchanged).")] string name,
        [Description("Description (default empty).")] string? description = null,
        [Description("Access token type (default ACCESS_TOKEN_TYPE_JWT).")] string access_token_type = "ACCESS_TOKEN_TYPE_JWT",
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("user_id", user_id) is { } idErr)
            return Task.FromResult<object>(new { ok = false, error = idErr });
        if (ZitadelValidation.ValidateName("name", name) is { } nErr)
            return Task.FromResult<object>(new { ok = false, error = nErr });
        if (ZitadelValidation.ValidateDescription(description) is { } dErr)
            return Task.FromResult<object>(new { ok = false, error = dErr });
        if (ZitadelValidation.ValidateEnum("access_token_type", access_token_type) is { } aErr)
            return Task.FromResult<object>(new { ok = false, error = aErr });

        var body = new
        {
            name,
            description = description ?? "",
            accessTokenType = access_token_type,
        };
        return ZitadelToolGuard.RunAsync(async () => await zitadel.UpdateMachineUserAsync(user_id, body, ct));
    }

    [McpServerTool(Name = "delete_machine_user", Destructive = true)]
    [Description("Delete a user BY ID via ZITADEL's RemoveUser API. Symmetric with create_machine_user — " +
        "use it to retire a machine (service) user the MCP provisioned. IRREVERSIBLE: the user, its login " +
        "names, keys, PATs and grants are removed and cannot be recovered. MUTATES.")]
    public static Task<object> DeleteMachineUser(
        ZitadelClient zitadel,
        [Description("User id to delete (irreversible).")] string user_id,
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("user_id", user_id) is { } err)
            return Task.FromResult<object>(new { ok = false, error = err });
        return ZitadelToolGuard.RunAsync(async () => await zitadel.DeleteMachineUserAsync(user_id, ct));
    }

    [McpServerTool(Name = "create_pat", Destructive = false)]
    [Description("Issue a Personal Access Token for a machine user. Returns {tokenId, token, details}. " +
        "MUTATES + SENSITIVE — the response carries a long-lived bearer token; treat the result as a secret.")]
    public static Task<object> CreatePat(
        ZitadelClient zitadel,
        [Description("User id.")] string user_id,
        [Description("Expiration (ISO-8601, default 2099-01-01T00:00:00Z).")] string expiration_iso = "2099-01-01T00:00:00Z",
        CancellationToken ct = default)
    {
        if (ZitadelValidation.ValidateId("user_id", user_id) is { } idErr)
            return Task.FromResult<object>(new { ok = false, error = idErr });
        if (ZitadelValidation.ValidateExpiration(expiration_iso) is { } expErr)
            return Task.FromResult<object>(new { ok = false, error = expErr });
        return ZitadelToolGuard.RunAsync(async () => await zitadel.CreatePatAsync(user_id, new { expirationDate = expiration_iso }, ct));
    }

    [McpServerTool(Name = "create_machine_key", Destructive = false)]
    [Description("Issue a JSON private key for a machine user and write it HOST-SIDE to " +
        "AGENT_KEY_DIR/<agent_file>.json (mode 0640) on the MCP host. Returns ONLY " +
        "{ok, userId, keyId, path, bytes} — the private key is NEVER returned or logged, so it never " +
        "enters any agent transcript. Use to provision a service identity's JWK. MUTATES + writes a secret to disk.")]
    public static async Task<object> CreateMachineKey(
        ZitadelClient zitadel,
        [Description("Machine user id (from create_machine_user).")] string user_id,
        [Description("Agent file basename, e.g. 'agent-journey-ux' → <AGENT_KEY_DIR>/agent-journey-ux.json.")] string agent_file,
        [Description("Expiration (ISO-8601, default 2099-01-01T00:00:00Z).")] string expiration_iso = "2099-01-01T00:00:00Z",
        CancellationToken ct = default)
    {
        // Fail-fast input validation BEFORE any HTTP call or disk write.
        if (ZitadelValidation.ValidateId("user_id", user_id) is { } idErr)
            return new { ok = false, error = idErr };
        if (ZitadelValidation.ValidateAgentFile(agent_file) is { } fileErr)
            return new { ok = false, error = fileErr };
        if (ZitadelValidation.ValidateExpiration(expiration_iso) is { } expErr)
            return new { ok = false, error = expErr };

        try
        {
            // KEY_TYPE_JSON + no publicKey ⇒ ZITADEL generates the keypair and returns the
            // private key file (base64) in `keyDetails`.
            var el = await zitadel.CreateMachineKeyAsync(
                user_id, new { type = "KEY_TYPE_JSON", expirationDate = expiration_iso }, ct).ConfigureAwait(false);

            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("keyDetails", out var kd) || kd.ValueKind != JsonValueKind.String)
                return new { ok = false, error = "ZITADEL response missing keyDetails" };

            var keyId = el.TryGetProperty("keyId", out var kid) ? kid.GetString() : null;

            byte[] keyFile;
            try { keyFile = Convert.FromBase64String(kd.GetString()!); }
            catch (FormatException) { return new { ok = false, error = "keyDetails is not valid base64" }; }

            Directory.CreateDirectory(zitadel.AgentKeyDir);
            var dest = Path.Combine(zitadel.AgentKeyDir, $"{agent_file}.json");
            await File.WriteAllBytesAsync(dest, keyFile, ct).ConfigureAwait(false);
            // 0640 — matches the agent-jwt read expectation. Best-effort on non-Unix.
#pragma warning disable CA1416 // SetUnixFileMode is unsupported on Windows — guarded by the try/catch.
            try { File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); }
            catch { /* best-effort */ }
#pragma warning restore CA1416

            // Return ONLY metadata — the key bytes are never serialised back to the caller.
            // The response key stays `userId` (result-contract field, unchanged for callers).
            return new { ok = true, userId = user_id, keyId, path = dest, bytes = keyFile.Length };
        }
        catch (ZitadelApiException ex)
        {
            // Route through the same scrub the ZitadelToolGuard applies, so a credential echoed
            // in the upstream body can never reach the caller from this hand-rolled catch either.
            return new { ok = false, status = ex.Status, error = ZitadelErrors.Sanitize(ex.Message) };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            return new { ok = false, status = (int?)null, error = ZitadelErrors.Sanitize($"{ex.GetType().Name}: {ex.Message}") };
        }
    }

    /// <summary>Guard against path traversal — the key filename must be a bare basename.
    /// Delegates to <see cref="ZitadelValidation.IsSafeBasename"/> (retained as a public
    /// method so the existing basename-guard test can call it directly).</summary>
    public static bool IsSafeBasename(string name) => ZitadelValidation.IsSafeBasename(name);
}
