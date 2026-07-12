using System.Text.Json;
using Cervello.Enrichment.Ports;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IVoiceprintRegistryClipReader"/> over the homelab <c>gdrive</c> MCP through the CT121
/// agentgateway — the SAME transport (<see cref="GatewayMcpClient"/>) + identity
/// (<c>agent-cervello-watcher</c>, minted per-call by <see cref="AgentJwtMinter"/>) that
/// <see cref="GdriveVoiceSampleUploader"/> (V4) and <see cref="GdriveVoiceprintRegistry"/> (V5) use.
///
/// <para><b>LIST</b> = <c>list_files(folderId = registry)</c> projected to <c>{fileId, name}</c> (folders
/// excluded). <b>DOWNLOAD</b> = the gdrive MCP <c>download_file_base64</c> tool, looped on
/// <c>offset</c>/<c>returnedBytes</c> until <c>eof</c>, base64-decoding each chunk's <c>content</c> — the
/// lossless binary path (never <c>download_file</c>, whose UTF-8 decode is lossy for audio bytes).</para>
///
/// <para><b>Wire note.</b> <see cref="GatewayMcpClient.CallToolAsync"/> returns the CONCATENATED text
/// content of the tool result, which for these gdrive tools is the tool's JSON object serialised as text
/// — so we <c>JsonDocument.Parse</c> the returned string, exactly as <see cref="GdriveVoiceprintRegistry"/>
/// does for <c>list_files</c>/<c>move_file</c>.</para>
///
/// <para><b>Known deploy dependency.</b> As with V4/V5, <c>agent-cervello-watcher</c> must be granted
/// <c>list_files</c> + <c>download_file_base64</c> at the CT121 gateway; until then these calls 403 and the
/// caller reports the failure (never fabricates a centroid).</para>
/// </summary>
public sealed class GdriveVoiceprintRegistryClipReader : IVoiceprintRegistryClipReader
{
    // The gdrive download_file_base64 tool caps a chunk at 4 MiB; a ~25 s registry clip fits in one chunk.
    // We still loop on eof so an unexpectedly large clip is reassembled byte-exact.
    private const int ChunkBytes = 4 * 1024 * 1024;
    private const int MaxChunks = 64; // 256 MiB ceiling — a voiceprint clip is never remotely this big.

    private readonly GatewayMcpClient _gateway;
    private readonly AgentJwtMinter _jwtMinter;
    private readonly EnrichmentConfig _cfg;
    private readonly Uri _gatewayUri;

    public GdriveVoiceprintRegistryClipReader(GatewayMcpClient gateway, AgentJwtMinter jwtMinter, EnrichmentConfig cfg)
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

    public async Task<IReadOnlyList<DriveFileEntry>> ListRegistryFolderAsync(CancellationToken ct = default)
    {
        var folderId = _cfg.VoiceprintsRegistryDriveFolderId;
        if (string.IsNullOrWhiteSpace(folderId))
            throw new InvalidOperationException(
                "CERVELLO_VOICEPRINTS_REGISTRY_DRIVE_FOLDER_ID is not set — the registry-pilot enrol " +
                "surface needs the operator's voiceprints/registry/ folder id to list its named clips.");

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
                // Exclude any nested folder (a folder is never a clip).
                var mime = f.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (mime == "application/vnd.google-apps.folder")
                    continue;
                result.Add(new DriveFileEntry(id, name));
            }
        }
        return result;
    }

    public async Task<ReadOnlyMemory<byte>> DownloadClipAsync(string fileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            throw new ArgumentException("fileId must be non-empty", nameof(fileId));

        using var buffer = new MemoryStream();
        long offset = 0;
        for (var chunk = 0; chunk < MaxChunks; chunk++)
        {
            var raw = await CallToolAsync(
                "download_file_base64", new { fileId, offset, maxBytes = ChunkBytes }, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var reason = root.TryGetProperty("error", out var e) ? e.GetString() : "download_file_base64 failed";
                throw new InvalidOperationException(reason ?? "download_file_base64 failed");
            }
            if (!root.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("download_file_base64 response carried no base64 content");

            var bytes = Convert.FromBase64String(contentEl.GetString()!);
            buffer.Write(bytes, 0, bytes.Length);

            var eof = !root.TryGetProperty("eof", out var eofEl) || eofEl.ValueKind != JsonValueKind.False;
            if (eof)
                break;
            offset += bytes.Length;
            if (bytes.Length == 0)
                break; // defensive: a non-eof empty chunk would otherwise spin.
        }

        if (buffer.Length == 0)
            throw new InvalidOperationException($"download_file_base64 for {fileId} returned no bytes");
        return buffer.ToArray();
    }
}
