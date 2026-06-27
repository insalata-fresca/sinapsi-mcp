using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Tools;

/// <summary>
/// ZITADEL machine (service) user lifecycle tools — the M2M identity-minting surface the
/// homelab secret-delivery canon depends on: create/update/delete a machine user, issue a
/// Personal Access Token, and issue a JSON private key written host-side (never returned to
/// the caller). The host injects the <see cref="ZitadelClient"/> and <see cref="ZitadelConfig"/>.
/// </summary>
[McpServerToolType]
public sealed class MachineUserTools
{
    [McpServerTool(Name = "create_machine_user", Destructive = false)]
    [Description("Create a machine (service) user. Returns {userId, details}. MUTATES. " +
        "access_token_type defaults to ACCESS_TOKEN_TYPE_JWT — agents that authenticate to the " +
        "agentgateway PEP MUST be JWT (a BEARER token is opaque and the gateway's JWKS validator " +
        "rejects it). Use ACCESS_TOKEN_TYPE_BEARER only for an introspection-based client.")]
    public static Task<object> CreateMachineUser(
        ZitadelClient zitadel,
        [Description("Username (loginName).")] string username,
        [Description("Display name (default = username).")] string? name = null,
        [Description("Description (default empty).")] string? description = null,
        [Description("Access token type — default ACCESS_TOKEN_TYPE_JWT.")] string accessTokenType = "ACCESS_TOKEN_TYPE_JWT",
        CancellationToken ct = default)
    {
        var body = new
        {
            userName        = username,
            name            = name ?? username,
            description     = description ?? "",
            accessTokenType,
        };
        return ZitadelToolGuard.RunAsync(async () => await zitadel.CreateMachineUserAsync(body, ct));
    }

    [McpServerTool(Name = "update_machine_user", Destructive = true)]
    [Description("Update a machine (service) user — name, description, and/or access-token type. " +
        "Set access_token_type=ACCESS_TOKEN_TYPE_JWT to make an agent's tokens validatable by the " +
        "agentgateway PEP. MUTATES.")]
    public static Task<object> UpdateMachineUser(
        ZitadelClient zitadel,
        [Description("Machine user id.")] string userId,
        [Description("Display name (ZITADEL's UpdateMachine requires it — pass the existing name to leave it unchanged).")] string name,
        [Description("Description (default empty).")] string? description = null,
        [Description("Access token type (default ACCESS_TOKEN_TYPE_JWT).")] string accessTokenType = "ACCESS_TOKEN_TYPE_JWT",
        CancellationToken ct = default)
    {
        var body = new
        {
            name,
            description = description ?? "",
            accessTokenType,
        };
        return ZitadelToolGuard.RunAsync(async () => await zitadel.UpdateMachineUserAsync(userId, body, ct));
    }

    [McpServerTool(Name = "delete_machine_user", Destructive = true)]
    [Description("Delete a user BY ID via ZITADEL's RemoveUser API. Symmetric with create_machine_user — " +
        "use it to retire a machine (service) user the MCP provisioned. IRREVERSIBLE: the user, its login " +
        "names, keys, PATs and grants are removed and cannot be recovered. MUTATES.")]
    public static Task<object> DeleteMachineUser(
        ZitadelClient zitadel,
        [Description("User id to delete (irreversible).")] string userId,
        CancellationToken ct = default)
        => ZitadelToolGuard.RunAsync(async () => await zitadel.DeleteMachineUserAsync(userId, ct));

    [McpServerTool(Name = "create_pat", Destructive = false)]
    [Description("Issue a Personal Access Token for a machine user. Returns {tokenId, token, details}. " +
        "MUTATES + SENSITIVE — the response carries a long-lived bearer token; treat the result as a secret.")]
    public static Task<object> CreatePat(
        ZitadelClient zitadel,
        [Description("User id.")] string userId,
        [Description("Expiration (ISO-8601, default 2099-01-01T00:00:00Z).")] string expirationIso = "2099-01-01T00:00:00Z",
        CancellationToken ct = default)
        => ZitadelToolGuard.RunAsync(async () => await zitadel.CreatePatAsync(userId, new { expirationDate = expirationIso }, ct));

    [McpServerTool(Name = "create_machine_key", Destructive = false)]
    [Description("Issue a JSON private key for a machine user and write it HOST-SIDE to " +
        "AGENT_KEY_DIR/<agent_file>.json (mode 0640) on the MCP host. Returns ONLY " +
        "{ok, userId, keyId, path, bytes} — the private key is NEVER returned or logged, so it never " +
        "enters any agent transcript. Use to provision an agent identity's JWK by-the-books. MUTATES + writes a secret to disk.")]
    public static async Task<object> CreateMachineKey(
        ZitadelClient zitadel,
        ZitadelConfig config,
        [Description("Machine user id (from create_machine_user).")] string userId,
        [Description("Agent file basename, e.g. 'agent-journey-ux' → <AGENT_KEY_DIR>/agent-journey-ux.json.")] string agentFile,
        [Description("Expiration (ISO-8601, default 2099-01-01T00:00:00Z).")] string expirationIso = "2099-01-01T00:00:00Z",
        CancellationToken ct = default)
    {
        if (!IsSafeBasename(agentFile))
            return new { ok = false, error = "agent_file must be a bare basename (no '/', '\\' or '..')" };

        try
        {
            // KEY_TYPE_JSON + no publicKey ⇒ ZITADEL generates the keypair and returns the
            // private key file (base64) in `keyDetails`.
            var el = await zitadel.CreateMachineKeyAsync(
                userId, new { type = "KEY_TYPE_JSON", expirationDate = expirationIso }, ct).ConfigureAwait(false);

            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("keyDetails", out var kd) || kd.ValueKind != JsonValueKind.String)
                return new { ok = false, error = "ZITADEL response missing keyDetails" };

            var keyId = el.TryGetProperty("keyId", out var kid) ? kid.GetString() : null;

            byte[] keyFile;
            try { keyFile = Convert.FromBase64String(kd.GetString()!); }
            catch (FormatException) { return new { ok = false, error = "keyDetails is not valid base64" }; }

            Directory.CreateDirectory(config.AgentKeyDir);
            var dest = Path.Combine(config.AgentKeyDir, $"{agentFile}.json");
            await File.WriteAllBytesAsync(dest, keyFile, ct).ConfigureAwait(false);
            // 0640 — matches the agent-jwt read expectation. Best-effort on non-Unix.
#pragma warning disable CA1416 // SetUnixFileMode is unsupported on Windows — guarded by the try/catch.
            try { File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); }
            catch { /* best-effort */ }
#pragma warning restore CA1416

            // Return ONLY metadata — the key bytes are never serialised back to the caller.
            return new { ok = true, userId, keyId, path = dest, bytes = keyFile.Length };
        }
        catch (ZitadelApiException ex)
        {
            return new { ok = false, status = ex.Status, error = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            return new { ok = false, status = (int?)null, error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    /// <summary>Guard against path traversal — the key filename must be a bare basename.</summary>
    public static bool IsSafeBasename(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..")
        && name == Path.GetFileName(name);
}
