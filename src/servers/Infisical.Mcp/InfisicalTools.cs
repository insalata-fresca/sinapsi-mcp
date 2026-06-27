using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Server;
using NATS.NKeys;

namespace Infisical.Mcp;

/// <summary>
/// A secret-issuance tool surface over an Infisical project. The design that keeps it
/// transcript-safe: for generated material the secret VALUE is produced SERVER-SIDE and
/// only NON-secret material (public keys, paths, names) is ever returned to the caller.
/// Secrets are organised under a two-level path: a group folder and a service folder
/// beneath it (e.g. <c>/web/api/DB_PASSWORD</c>).
/// </summary>
[McpServerToolType]
public sealed class InfisicalTools(InfisicalClient client, InfisicalOptions opt)
{
    [McpServerTool, Description(
        "Issue a NATS user nkey for a service: generate the Ed25519 keypair SERVER-SIDE, store the "
        + "SEED in Infisical at /<group>/<service>/NATS_NKEY_SEED, and return ONLY the public key (U...). "
        + "The seed never leaves the MCP. Use to mint a NATS client identity without handling the seed.")]
    public async Task<string> issue_nats_nkey(
        [Description("Group folder, e.g. web")] string group,
        [Description("Service name, e.g. api")] string service,
        CancellationToken ct)
    {
        using var kp = KeyPair.CreatePair(PrefixByte.User);
        var seed = kp.GetSeed();
        var pub = kp.GetPublicKey();
        await client.EnsureFolderAsync(group, service, ct).ConfigureAwait(false);
        await client.SetSecretAsync($"/{group}/{service}", "NATS_NKEY_SEED", seed, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            public_key = pub,
            path = $"/{group}/{service}/NATS_NKEY_SEED",
            env = opt.EnvName,
        });
    }

    [McpServerTool, Description(
        "Generate a random secret SERVER-SIDE (hex; default 32 bytes) and store it at "
        + "/<group>/<service>/<name>. Returns a confirmation only — the value never leaves the MCP.")]
    public async Task<string> issue_random_secret(string group, string service, string name, int bytes, CancellationToken ct)
    {
        var n = bytes <= 0 ? 32 : bytes;
        var val = Convert.ToHexString(RandomNumberGenerator.GetBytes(n)).ToLowerInvariant();
        await client.EnsureFolderAsync(group, service, ct).ConfigureAwait(false);
        await client.SetSecretAsync($"/{group}/{service}", name, val, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { stored = $"/{group}/{service}/{name}", bytes = n });
    }

    [McpServerTool, Description(
        "Store a caller-supplied secret value at /<group>/<service>/<name>. For vendor-issued tokens. "
        + "NOTE: the value passes through the caller — prefer issue_random_secret / issue_nats_nkey for "
        + "generated material so nothing sensitive transits the transcript.")]
    public async Task<string> set_secret(string group, string service, string name, string value, CancellationToken ct)
    {
        await client.EnsureFolderAsync(group, service, ct).ConfigureAwait(false);
        await client.SetSecretAsync($"/{group}/{service}", name, value, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { stored = $"/{group}/{service}/{name}" });
    }

    [McpServerTool, Description("List secret NAMES (never values) at /<group>/<service>.")]
    public async Task<string> list_secrets(string group, string service, CancellationToken ct)
    {
        var names = await client.ListSecretNamesAsync($"/{group}/{service}", ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { path = $"/{group}/{service}", names });
    }
}
