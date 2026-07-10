using System.Text.Json;
using Cervello.Enrichment.Ports;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IVoiceprintRegistryDrive"/> over the homelab <c>gdrive</c> MCP through the CT121
/// agentgateway — the SAME transport (<see cref="GatewayMcpClient"/>) + identity
/// (<c>agent-cervello-watcher</c>, minted per-call by <see cref="AgentJwtMinter"/>) that
/// <see cref="GdriveVoiceSampleUploader"/> (V4) and <see cref="Cervello.Watcher.Drive.McpGdriveClient"/>
/// already use (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V5, §8).
///
/// <para><b>LIST</b> = <c>gdrive_list_files(folderId)</c> over the configured <c>voiceprints/</c>
/// folder, projected to <c>{fileId, name}</c>. <b>MOVE</b> = the V3 <c>move_file</c> tool
/// (<c>addParents = registry</c>, <c>removeParents = voiceprints</c>). The <c>registry/</c> subfolder
/// is resolved once (config folder id, or create-if-absent under the voiceprints parent) and cached.</para>
///
/// <para><b>Known deploy dependency.</b> As with V4's uploader, <c>agent-cervello-watcher</c> must be
/// grant-widened at the CT121 gateway to include <c>list_files</c> + <c>move_file</c> + <c>create_folder</c>
/// (a home-server S19-style identity task, design §8) before these calls succeed at runtime; until
/// then they 403 and the poller leaves the candidate unresolved to retry.</para>
/// </summary>
public sealed class GdriveVoiceprintRegistry : IVoiceprintRegistryDrive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly GatewayMcpClient _gateway;
    private readonly AgentJwtMinter _jwtMinter;
    private readonly EnrichmentConfig _cfg;
    private readonly Uri _gatewayUri;

    private readonly SemaphoreSlim _registryGate = new(1, 1);
    private string? _resolvedRegistryFolderId;

    public GdriveVoiceprintRegistry(GatewayMcpClient gateway, AgentJwtMinter jwtMinter, EnrichmentConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(jwtMinter);
        ArgumentNullException.ThrowIfNull(cfg);
        _gateway = gateway;
        _jwtMinter = jwtMinter;
        _cfg = cfg;
        _gatewayUri = new Uri(cfg.GdriveGatewayUrl);
    }

    private async Task<string> CallToolAsync(string toolName, object args, CancellationToken ct)
    {
        var jwt = await _jwtMinter.MintAsync(_cfg.VoiceprintsWatcherAgent, ct).ConfigureAwait(false);
        return await _gateway.CallToolAsync(_gatewayUri, jwt, toolName, args, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DriveFileEntry>> ListVoiceprintsFolderAsync(CancellationToken ct = default)
    {
        var folderId = _cfg.VoiceprintsDriveFolderId;
        if (string.IsNullOrWhiteSpace(folderId))
            throw new InvalidOperationException(
                "CERVELLO_VOICEPRINTS_DRIVE_FOLDER_ID is not set — the V5 rename-poller needs the operator's " +
                "voiceprints/ folder id to list it for renames.");

        var raw = await CallToolAsync(
            "list_files", new { folderId, pageSize = 1000, includeTrashed = false }, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var reason = root.TryGetProperty("error", out var e) ? e.GetString() : "list_files failed";
            throw new InvalidOperationException(reason ?? "list_files failed");
        }

        var result = new List<DriveFileEntry>();
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                if (!f.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                    continue;
                var id = idEl.GetString()!;
                var name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                // Exclude the registry/ subfolder itself (a folder, never a sample).
                var mime = f.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (mime == "application/vnd.google-apps.folder")
                    continue;
                result.Add(new DriveFileEntry(id, name));
            }
        }
        return result;
    }

    public async Task MoveToRegistryAsync(string fileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            throw new ArgumentException("fileId must be non-empty", nameof(fileId));

        var registryFolderId = await ResolveRegistryFolderIdAsync(ct).ConfigureAwait(false);
        var voiceprintsFolderId = _cfg.VoiceprintsDriveFolderId;

        // move_file: add the registry parent, remove the voiceprints parent (a real move, not a copy).
        var raw = await CallToolAsync(
            "move_file",
            new
            {
                fileId,
                destFolderId = registryFolderId,
                removeFolderId = string.IsNullOrWhiteSpace(voiceprintsFolderId) ? null : voiceprintsFolderId,
            },
            ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var reason = root.TryGetProperty("error", out var e) ? e.GetString() : "move_file failed";
            throw new InvalidOperationException(reason ?? "move_file failed");
        }
    }

    private async Task<string> ResolveRegistryFolderIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.VoiceprintsRegistryDriveFolderId))
            return _cfg.VoiceprintsRegistryDriveFolderId;

        if (_resolvedRegistryFolderId is not null)
            return _resolvedRegistryFolderId;

        await _registryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_resolvedRegistryFolderId is not null)
                return _resolvedRegistryFolderId;

            var parent = string.IsNullOrWhiteSpace(_cfg.VoiceprintsDriveFolderId)
                ? (string?)null
                : _cfg.VoiceprintsDriveFolderId;
            var raw = await CallToolAsync(
                "create_folder", new { name = "registry", parentFolderId = parent }, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var reason = root.TryGetProperty("error", out var e) ? e.GetString() : "create_folder failed";
                throw new InvalidOperationException(reason ?? "create_folder failed");
            }
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("create_folder response carried no folder id");

            _resolvedRegistryFolderId = idEl.GetString()!;
            return _resolvedRegistryFolderId;
        }
        finally
        {
            _registryGate.Release();
        }
    }
}
