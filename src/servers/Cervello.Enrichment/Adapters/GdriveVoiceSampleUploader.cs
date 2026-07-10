using System.Text.Json;
using Cervello.Enrichment.Ports;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IVoiceSampleUploader"/> over the existing homelab <c>gdrive</c> MCP, called
/// through the CT121 agentgateway — the SAME transport (<see cref="GatewayMcpClient"/>) + identity
/// (<c>agent-cervello-watcher</c>, minted per-call by <see cref="AgentJwtMinter"/>) that
/// <see cref="Cervello.Watcher.Drive.McpGdriveClient"/> already uses to read Drive (design
/// <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V4, §8: <c>create_folder</c> +
/// <c>upload_file</c> under <c>agent-cervello-watcher</c>). Agent-free — no Google credential in
/// this process at all.
///
/// <para><b>Known deploy dependency.</b> <c>agent-cervello-watcher</c> is currently READ-ONLY at the
/// CT121 gateway; a separate home-server grant-widening (S19-style identity task, design §8 "the
/// scoped agent-cervello-watcher grant must be widened to include them") is required before this
/// adapter's calls actually succeed at runtime. This class is built and wired regardless — the
/// upload/create_folder/delete_file calls will 403 at the gateway until that grant lands; V4's
/// orchestrator treats that failure the same as any other terminal upload failure (skip, log, never
/// fabricate a file id).</para>
///
/// <para><b>Folder resolution, cached per-process.</b> The destination folder id is resolved ONCE
/// (create-if-absent under the configured parent) and cached for the adapter's lifetime — every
/// subsequent upload in the same process reuses it, avoiding a redundant <c>create_folder</c> round
/// trip per clip in a ~15-clip generate-samples run.</para>
/// </summary>
public sealed class GdriveVoiceSampleUploader : IVoiceSampleUploader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly GatewayMcpClient _gateway;
    private readonly AgentJwtMinter _jwtMinter;
    private readonly EnrichmentConfig _cfg;
    private readonly Uri _gatewayUri;

    private readonly SemaphoreSlim _folderGate = new(1, 1);
    private string? _resolvedFolderId;

    public GdriveVoiceSampleUploader(GatewayMcpClient gateway, AgentJwtMinter jwtMinter, EnrichmentConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(jwtMinter);
        ArgumentNullException.ThrowIfNull(cfg);
        _gateway = gateway;
        _jwtMinter = jwtMinter;
        _cfg = cfg;
        _gatewayUri = new Uri(cfg.GdriveGatewayUrl);
    }

    private async Task<string> MintAsync(CancellationToken ct) =>
        await _jwtMinter.MintAsync(_cfg.VoiceprintsWatcherAgent, ct).ConfigureAwait(false);

    private async Task<string> CallToolAsync(string toolName, object args, CancellationToken ct)
    {
        var jwt = await MintAsync(ct).ConfigureAwait(false);
        return await _gateway.CallToolAsync(_gatewayUri, jwt, toolName, args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the <c>voiceprints/</c> destination folder id: the configured
    /// <see cref="EnrichmentConfig.VoiceprintsDriveFolderId"/> when set directly (the operator's
    /// documented folder id), else create-if-absent via <c>gdrive_create_folder</c> under the
    /// configured recordings parent. Cached after the first successful resolution.
    /// </summary>
    private async Task<string> ResolveFolderIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.VoiceprintsDriveFolderId))
            return _cfg.VoiceprintsDriveFolderId;

        if (_resolvedFolderId is not null)
            return _resolvedFolderId;

        await _folderGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_resolvedFolderId is not null)
                return _resolvedFolderId;

            var raw = await CallToolAsync(
                "create_folder",
                new { name = "voiceprints", parentFolderId = (string?)null },
                ct).ConfigureAwait(false);
            var id = ExtractId(raw, "create_folder");
            _resolvedFolderId = id;
            return id;
        }
        finally
        {
            _folderGate.Release();
        }
    }

    public async Task<string> UploadAsync(
        string fileName, ReadOnlyMemory<byte> clipBytes, string mimeType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName must be non-empty", nameof(fileName));
        if (clipBytes.IsEmpty)
            throw new ArgumentException("clipBytes must be non-empty", nameof(clipBytes));
        if (string.IsNullOrWhiteSpace(mimeType))
            throw new ArgumentException("mimeType must be non-empty", nameof(mimeType));

        var folderId = await ResolveFolderIdAsync(ct).ConfigureAwait(false);
        var contentBase64 = Convert.ToBase64String(clipBytes.Span);

        var raw = await CallToolAsync(
            "upload_file",
            new { name = fileName, contentBase64, mimeType, folderId },
            ct).ConfigureAwait(false);
        return ExtractId(raw, "upload_file");
    }

    public async Task DeleteAsync(string driveFileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveFileId)) return;
        try
        {
            await CallToolAsync("delete_file", new { fileId = driveFileId, permanent = false }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort cleanup (port contract): an already-gone/unreachable file must never fail
            // the caller's regenerate cycle. The stale candidate row is still cleared by the store.
        }
    }

    private static string ExtractId(string raw, string toolName)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var reason = root.TryGetProperty("error", out var e) ? e.GetString() : $"{toolName} failed";
            throw new InvalidOperationException(reason ?? $"{toolName} failed");
        }
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            return idEl.GetString()!;
        throw new InvalidOperationException($"{toolName} response carried no file id");
    }
}
