using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cervello.Watcher.Domain;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;

namespace Cervello.Watcher.Drive;

/// <summary>
/// <see cref="IDriveClient"/> over the existing homelab <c>gdrive</c> MCP, called
/// through the CT121 agentgateway (M6-refine — supersedes the read-only Google
/// service-account design, D1/D2). Auth is a scoped agentgateway machine identity
/// (<see cref="WatcherConfig.WatcherAgent"/>, minted per-call by
/// <see cref="AgentJwtMinter"/>) — agent-free, no Google credential in this
/// process at all.
///
/// WATCH divergence: the gdrive MCP exposes no Drive Changes API (no cursor). This
/// client emulates the Changes-feed shape <see cref="IDriveClient"/> requires by
/// polling <c>gdrive_list_files(folderId)</c> each cycle and diffing the result
/// against a snapshot (fileId -&gt; md5) carried OPAQUELY inside the page token
/// itself (base64 JSON) — <see cref="WatchWorker"/> and <see cref="IStateStore"/>
/// never need to know the encoding, they just persist whatever string this client
/// hands back (unchanged cursor discipline). Same idempotency keys downstream
/// (<c>drive:&lt;fileId&gt;:&lt;md5&gt;</c> / <c>rec:&lt;id&gt;:&lt;audio_sha256&gt;</c>) —
/// a diff-detected "change" collapses to the same replay-no-op path as before.
/// </summary>
public sealed class McpGdriveClient : IDriveClient
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly GatewayMcpClient _gateway;
    private readonly AgentJwtMinter _jwtMinter;
    private readonly WatcherConfig _cfg;
    private readonly Uri _gatewayUri;

    public McpGdriveClient(GatewayMcpClient gateway, AgentJwtMinter jwtMinter, WatcherConfig cfg)
    {
        _gateway = gateway;
        _jwtMinter = jwtMinter;
        _cfg = cfg;
        _gatewayUri = new Uri(cfg.GatewayUrl);
    }

    // ---- snapshot (fileId -> md5) carried opaquely inside the page token ----
    private sealed record Snapshot([property: JsonPropertyName("files")] Dictionary<string, string?> Files);

    private static string EncodeSnapshot(Snapshot s) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(s, _json));

    private static Snapshot DecodeSnapshot(string token)
    {
        try
        {
            var bytes = Convert.FromBase64String(token);
            return JsonSerializer.Deserialize<Snapshot>(bytes, _json)
                   ?? new Snapshot(new Dictionary<string, string?>(StringComparer.Ordinal));
        }
        catch (Exception) when (token is not null)
        {
            // A foreign/corrupt token is treated as an empty snapshot rather than a
            // crash — worst case is a one-time full re-detection (every file looks
            // "new"), which the downstream idempotency ledger (drive:<fileId>:<md5>)
            // collapses to a no-op for anything already staged.
            return new Snapshot(new Dictionary<string, string?>(StringComparer.Ordinal));
        }
    }

    private async Task<string> MintAsync(CancellationToken ct) =>
        await _jwtMinter.MintAsync(_cfg.WatcherAgent, ct).ConfigureAwait(false);

    /// <summary>
    /// Startup-only helper (NOT part of <see cref="IDriveClient"/>): resolve
    /// <see cref="WatcherConfig.FolderPath"/> (e.g. <c>cervello/recordings</c>) to
    /// its Drive folder id via <c>gdrive_search_files</c>, when
    /// <see cref="WatcherConfig.FolderId"/> was not set directly. The last path
    /// segment is matched by name among Drive folders; ambiguity (0 or &gt;1
    /// match) is a fail-closed startup error — silently picking a folder would
    /// risk watching the wrong tree.
    /// </summary>
    public async Task<string> ResolveFolderIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.FolderId))
            return _cfg.FolderId;

        var segments = _cfg.FolderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var leafName = segments.Length > 0 ? segments[^1] : _cfg.FolderPath;
        var escaped = leafName.Replace("\\", "\\\\").Replace("'", "\\'");
        var query = $"name = '{escaped}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";

        var jwt = await MintAsync(ct).ConfigureAwait(false);
        var raw = await _gateway.CallToolAsync(
            _gatewayUri, jwt, "search_files", new { query, pageSize = 10 }, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var reason = root.TryGetProperty("error", out var e) ? e.GetString() : "search_files failed";
            throw new InvalidOperationException(reason ?? "search_files failed");
        }

        var matches = new List<string>();
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var f in files.EnumerateArray())
                if (f.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    matches.Add(idEl.GetString()!);

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"no Drive folder named '{leafName}' found for FolderPath '{_cfg.FolderPath}' " +
                "(set CERVELLO_WATCHER_FOLDER_ID to skip resolution)"),
            _ => throw new InvalidOperationException(
                $"{matches.Count} Drive folders named '{leafName}' found for FolderPath '{_cfg.FolderPath}' " +
                "— ambiguous; set CERVELLO_WATCHER_FOLDER_ID explicitly"),
        };
    }

    /// <summary>Bootstrap cursor: an empty snapshot — the first cycle sees every current file as "new".</summary>
    public Task<string> GetStartPageTokenAsync(CancellationToken ct) =>
        Task.FromResult(EncodeSnapshot(new Snapshot(new Dictionary<string, string?>(StringComparer.Ordinal))));

    /// <summary>
    /// One "page" = one full <c>gdrive_list_files(folderId)</c> listing, diffed
    /// against the snapshot decoded from <paramref name="pageToken"/>. New / md5-
    /// changed files become non-removed <see cref="DriveChange"/> entries; files
    /// present in the OLD snapshot but absent from the fresh listing (trashed or
    /// deleted — gdrive_list_files excludes trashed by default) become
    /// <c>removed:true</c> entries so <see cref="WatchWorker"/>'s existing
    /// removed/trashed skip path fires unchanged. Always a single page (no
    /// nextPageToken) — <see cref="WatchWorker"/> only loops on nextPageToken,
    /// which is never set here.
    /// </summary>
    public async Task<ChangePage> ListChangesAsync(string pageToken, CancellationToken ct)
    {
        var before = DecodeSnapshot(pageToken);
        var folderId = _cfg.FolderId
            ?? throw new InvalidOperationException(
                "McpGdriveClient requires a resolved FolderId (WatcherConfig.FolderId) — " +
                "resolve cfg.FolderPath via gdrive_search_files at startup before polling.");

        var jwt = await MintAsync(ct).ConfigureAwait(false);
        var raw = await _gateway.CallToolAsync(
            _gatewayUri, jwt, "list_files",
            new { folderId, pageSize = 1000, includeTrashed = false }, ct).ConfigureAwait(false);

        var files = ParseFileList(raw);

        var after = new Dictionary<string, string?>(StringComparer.Ordinal);
        var changes = new List<DriveChange>();

        foreach (var f in files)
        {
            after[f.Id] = f.Md5;
            var isNew = !before.Files.TryGetValue(f.Id, out var priorMd5);
            var changed = !isNew && !string.Equals(priorMd5, f.Md5, StringComparison.Ordinal);
            if (isNew || changed)
                changes.Add(ToDriveChange(f, folderId, removed: false));
        }

        // Anything that WAS in the snapshot but is no longer in the fresh listing
        // is treated as removed (trashed/deleted/moved out of the folder).
        foreach (var (fileId, _) in before.Files)
        {
            if (!after.ContainsKey(fileId))
                changes.Add(new DriveChange(
                    fileId: fileId, name: null, mimeType: null, md5: null, size: null,
                    createdTime: null, modifiedTime: null,
                    parents: new[] { folderId }, removed: true, trashed: false));
        }

        var newToken = EncodeSnapshot(new Snapshot(after));
        return new ChangePage(changes, NextPageToken: null, NewStartPageToken: newToken);
    }

    public async Task<DriveChange?> GetMetadataAsync(string fileId, CancellationToken ct)
    {
        var jwt = await MintAsync(ct).ConfigureAwait(false);
        var raw = await _gateway.CallToolAsync(
            _gatewayUri, jwt, "get_file_metadata", new { fileId }, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            return null; // gdrive tool error envelope ({ok:false,...}) -> treat as gone

        var meta = ParseFile(root);
        return ToDriveChange(meta, folderId: null, removed: false);
    }

    /// <summary>
    /// Stream the file's bytes into <paramref name="destination"/> via
    /// <c>download_file_base64</c>, looping 4 MiB chunks (the tool's documented
    /// per-call ceiling) until <c>eof</c>. Lossless for any binary size — the
    /// tool's own contract. No base64 is written to disk; each chunk is decoded
    /// in memory and written straight through.
    /// </summary>
    public async Task DownloadMediaAsync(string fileId, Stream destination, CancellationToken ct)
    {
        const int chunkBytes = 4 * 1024 * 1024;
        long offset = 0;
        while (true)
        {
            var jwt = await MintAsync(ct).ConfigureAwait(false);
            string raw;
            try
            {
                raw = await _gateway.CallToolAsync(
                    _gatewayUri, jwt, "download_file_base64",
                    new { fileId, offset, maxBytes = chunkBytes }, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException e)
            {
                // GatewayMcpClient failures embed the upstream HTTP status in the
                // message ("initialize: 503 ..." / "tools/call: 500 ..."); 5xx is
                // worth retrying under the same drive:<fileId>:<md5> key, anything
                // else (4xx, unparseable) is terminal.
                throw new DriveMediaException(e.Message, IsRetryableStatus(e.Message));
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                // gdrive's own {ok:false,error} envelope carries no HTTP status — the
                // gdrive MCP only ever produces this for a resolved-but-bad request
                // (missing file, validation failure), which is terminal, not transient.
                var reason = root.TryGetProperty("error", out var e) ? e.GetString() : "download_file_base64 failed";
                throw new DriveMediaException(reason ?? "download_file_base64 failed", false);
            }

            var content = root.GetProperty("content").GetString() ?? "";
            var bytes = Convert.FromBase64String(content);
            if (bytes.Length > 0)
                await destination.WriteAsync(bytes, ct).ConfigureAwait(false);

            var returned = root.TryGetProperty("returnedBytes", out var rb) ? rb.GetInt64() : bytes.LongLength;
            var eof = root.TryGetProperty("eof", out var eofEl) && eofEl.ValueKind == JsonValueKind.True;

            if (eof || returned <= 0)
                break;
            offset += returned;
        }
    }

    /// <summary>True if a GatewayMcpClient exception message names a 5xx (or an unnamed/transport-level) status.</summary>
    private static bool IsRetryableStatus(string message)
    {
        foreach (var token in message.Split(' ', ':'))
            if (int.TryParse(token, out var code) && code is >= 500 and <= 599)
                return true;
        return false;
    }

    // ---- gdrive tool-response parsing ----

    private sealed record GFile(string Id, string? Name, string? MimeType, string? Md5, long? Size,
        DateTimeOffset? CreatedTime, DateTimeOffset? ModifiedTime, IReadOnlyList<string>? Parents, bool Trashed);

    private static List<GFile> ParseFileList(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var reason = root.TryGetProperty("error", out var e) ? e.GetString() : "list_files failed";
            throw new InvalidOperationException(reason ?? "list_files failed");
        }

        var result = new List<GFile>();
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var f in files.EnumerateArray())
                result.Add(ParseFile(f));
        return result;
    }

    private static GFile ParseFile(JsonElement f)
    {
        string? S(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? L(string prop) => f.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        DateTimeOffset? D(string prop) => S(prop) is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;

        var id = S("id") ?? throw new InvalidOperationException("gdrive file entry missing id");
        IReadOnlyList<string>? parents = null;
        if (f.TryGetProperty("parents", out var p) && p.ValueKind == JsonValueKind.Array)
            parents = p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!).ToList();
        var trashed = f.TryGetProperty("trashed", out var t) && t.ValueKind == JsonValueKind.True;

        return new GFile(id, S("name"), S("mimeType"), S("md5Checksum"), L("size"), D("createdTime"), D("modifiedTime"), parents, trashed);
    }

    private static DriveChange ToDriveChange(GFile f, string? folderId, bool removed) => new(
        fileId: f.Id,
        name: f.Name,
        mimeType: f.MimeType,
        md5: f.Md5,
        size: f.Size,
        createdTime: f.CreatedTime,
        modifiedTime: f.ModifiedTime,
        parents: f.Parents ?? (folderId is not null ? new[] { folderId } : Array.Empty<string>()),
        removed: removed,
        trashed: f.Trashed);
}
