using System.ComponentModel;
using System.Text;
using Google.Apis.Drive.v3;
using ModelContextProtocol.Server;
using DriveData = Google.Apis.Drive.v3.Data;

namespace Gdrive.Mcp;

// ---------------------------------------------------------------------------
// Google Drive CRUD surface (12 tools). Hardening patterns (mirroring the
// StepCa.Mcp exemplar for an HTTP-backed server):
//   * Every tool validates its parameters via GdriveValidation BEFORE any HTTP
//     call is issued, returning a structured {ok:false, error} envelope on bad
//     input (never a thrown exception through the transport).
//   * Every surfaced upstream/error string routes through GdriveErrors.Sanitize
//     so an OAuth/refresh token can never leak in an error message.
//   * The per-request HttpClient.Timeout is clamped in DriveClientFactory from
//     the fail-closed GdriveConfig ceiling.
// ---------------------------------------------------------------------------

/// <summary>
/// Google Drive CRUD surface. A managed Drive connector typically lacks update + delete
/// (and can't be extended); this self-hosted MCP exposes the full lifecycle. The
/// <see cref="DriveService"/> is a DI singleton (authenticated once at startup).
/// </summary>
[McpServerToolType]
public sealed class DriveTools
{
    private const string FileFields =
        "id,name,mimeType,modifiedTime,createdTime,size,parents,owners(displayName,emailAddress),trashed,webViewLink";

    private const string FolderMimeType = "application/vnd.google-apps.folder";

    // Uniform structured-error envelope. Callers can rely on ok==false + a
    // sanitised error string for EVERY failure path (validation and upstream).
    private static object Err(string reason) => new { ok = false, error = GdriveErrors.Sanitize(reason) };

    [McpServerTool(Name = "list_files")]
    [Description("List files in Drive, newest first. Optionally scope to a parent folder id. Returns id/name/mimeType/modifiedTime/size.")]
    public static async Task<object> ListFiles(
        DriveService drive,
        [Description("Optional parent folder id to list children of. Omit for all files.")] string? folderId = null,
        [Description("Max files to return (1-1000). Default 50.")] int pageSize = 50,
        [Description("Include files in the trash. Default false.")] bool includeTrashed = false)
    {
        if (GdriveValidation.ValidateFolderId(folderId) is { } folderErr) return Err(folderErr);

        try
        {
            var req = drive.Files.List();
            req.PageSize = Math.Clamp(pageSize, 1, 1000);
            req.OrderBy = "modifiedTime desc";
            req.Fields = $"files({FileFields}),nextPageToken";
            var clauses = new List<string>();
            if (!string.IsNullOrWhiteSpace(folderId)) clauses.Add($"'{Escape(folderId)}' in parents");
            if (!includeTrashed) clauses.Add("trashed = false");
            if (clauses.Count > 0) req.Q = string.Join(" and ", clauses);

            var res = await req.ExecuteAsync();
            return new { count = res.Files.Count, files = res.Files.Select(Summarize), nextPageToken = res.NextPageToken };
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "search_files")]
    [Description("Search Drive with a raw Drive query string (e.g. \"name contains 'budget' and mimeType='application/pdf'\"). See Google Drive API 'q' syntax.")]
    public static async Task<object> SearchFiles(
        DriveService drive,
        [Description("Drive API query ('q') expression.")] string query,
        [Description("Max files to return (1-1000). Default 50.")] int pageSize = 50)
    {
        if (GdriveValidation.ValidateQuery(query) is { } queryErr) return Err(queryErr);

        try
        {
            var req = drive.Files.List();
            req.PageSize = Math.Clamp(pageSize, 1, 1000);
            req.Fields = $"files({FileFields}),nextPageToken";
            req.Q = query;
            var res = await req.ExecuteAsync();
            return new { count = res.Files.Count, files = res.Files.Select(Summarize), nextPageToken = res.NextPageToken };
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "get_file_metadata")]
    [Description("Get full metadata for a single file by id.")]
    public static async Task<object> GetFileMetadata(
        DriveService drive,
        [Description("The Drive file id.")] string fileId)
    {
        if (GdriveValidation.ValidateFileId(fileId) is { } idErr) return Err(idErr);

        try
        {
            var req = drive.Files.Get(fileId);
            req.Fields = FileFields;
            return Summarize(await req.ExecuteAsync());
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // 4 MiB default chunk. Sized to a modest container memory budget: a single call buffers the
    // chunk bytes + its ~1.33x base64 string + the JSON-serialised response, and an 8 MiB chunk
    // tipped that envelope over (verified — OOM-restarted the unit; 4 MiB has comfortable headroom).
    // For tens-of-MB artifacts prefer download_to_url (constant memory, no base64 in context).
    private const int DefaultChunkBytes = 4 * 1024 * 1024;
    // Hard per-call ceiling — keep base64 responses well under the transport cap AND the memory budget.
    private const int MaxChunkBytes = 4 * 1024 * 1024;

    [McpServerTool(Name = "download_file")]
    [Description("Download a TEXT file's content and return it as a UTF-8 string (response includes encoding=\"utf-8\"). For BINARY files (firmware .img/.bin, archives, images) this is LOSSY — use download_file_base64 (lossless, chunked) or download_to_url (server-side stream) instead. Google-native docs (Docs/Sheets) must be exported, not downloaded — use the Drive UI for those.")]
    public static async Task<object> DownloadFile(
        DriveService drive,
        [Description("The Drive file id.")] string fileId,
        [Description("Max bytes to return (guards against huge files). Default 1048576 (1 MiB).")] int maxBytes = 1_048_576)
    {
        if (GdriveValidation.ValidateFileId(fileId) is { } idErr) return Err(idErr);
        if (maxBytes < 1) maxBytes = 1;

        try
        {
            using var ms = new MemoryStream();
            await drive.Files.Get(fileId).DownloadAsync(ms);
            var bytes = ms.ToArray();
            var truncated = bytes.Length > maxBytes;
            var slice = truncated ? bytes.AsSpan(0, maxBytes).ToArray() : bytes;
            return new { fileId, bytes = bytes.Length, truncated, encoding = "utf-8", content = Encoding.UTF8.GetString(slice) };
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "download_file_base64")]
    [Description("Download a file LOSSLESSLY as base64 over an HTTP byte range — works for ANY binary (firmware .img/.bin, archives, images) and ANY size. Returns {fileId, offset, returnedBytes, totalSize, encoding:\"base64\", content (base64 of just this range), eof}. To fetch a large file, loop: start offset=0, then offset += returnedBytes, base64-decode and concatenate each chunk, stop when eof=true. The reassembled bytes are byte-exact. Default + max chunk 4 MiB (sized to the container memory; larger requests are clamped). For tens-of-MB artifacts prefer download_to_url.")]
    public static async Task<object> DownloadFileBase64(
        DriveService drive,
        [Description("The Drive file id.")] string fileId,
        [Description("Byte offset to start this chunk at. Default 0.")] long offset = 0,
        [Description("Max bytes for this chunk (1 .. 4194304). Default 4194304 (4 MiB).")] int maxBytes = DefaultChunkBytes)
    {
        if (GdriveValidation.ValidateFileId(fileId) is { } idErr) return Err(idErr);
        if (offset < 0) offset = 0;
        var count = Math.Clamp(maxBytes, 1, MaxChunkBytes);

        try
        {
            // Authoritative total size (also lets us short-circuit reads past EOF and clamp the range end).
            var meta = drive.Files.Get(fileId);
            meta.Fields = "size";
            meta.SupportsAllDrives = true;
            long? totalSize = (await meta.ExecuteAsync()).Size;

            if (totalSize is long known && offset >= known)
                return new { fileId, offset, returnedBytes = 0, totalSize, encoding = "base64", content = "", eof = true };

            // Don't ask past EOF — keeps the final chunk a clean 206 instead of risking a 416.
            if (totalSize is long sz) count = (int)Math.Min((long)count, sz - offset);

            var body = await DriveMedia.FetchRangeAsync(drive, fileId, offset, count, CancellationToken.None);
            var eof = totalSize is long t ? offset + body.LongLength >= t : body.Length < count;

            return new
            {
                fileId,
                offset,
                returnedBytes = body.Length,
                totalSize,
                encoding = "base64",
                content = Convert.ToBase64String(body),
                eof,
            };
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "download_to_url")]
    [Description("Best path for large binaries: stage a server-side streaming download and return a short-lived internal HTTP URL. The MCP host streams the file straight from Drive (constant memory) — no base64 ever passes through the model context. wget/curl the returned url from any host that can reach the MCP backend's configured download base URL. Returns {fileId, name, size, url, expiresInSeconds, expiresAt}. The url is an unguessable single-capability token valid for a short window; re-call to mint a fresh one. Use download_file_base64 instead from environments without network reach to that url.")]
    public static async Task<object> DownloadToUrl(
        DriveService drive,
        DownloadTicketStore tickets,
        GdriveConfig cfg,
        [Description("The Drive file id.")] string fileId)
    {
        if (GdriveValidation.ValidateFileId(fileId) is { } idErr) return Err(idErr);

        try
        {
            var meta = drive.Files.Get(fileId);
            meta.Fields = "id,name,size";
            meta.SupportsAllDrives = true;
            var f = await meta.ExecuteAsync();

            var ttl = TimeSpan.FromSeconds(cfg.DownloadTtlSeconds);
            var ticket = tickets.Issue(f.Id, f.Name, f.Size, ttl);

            return new
            {
                fileId = f.Id,
                name = f.Name,
                size = f.Size,
                url = $"{cfg.DownloadBaseUrl.TrimEnd('/')}/gdrive-dl/{ticket.Token}",
                expiresInSeconds = cfg.DownloadTtlSeconds,
                expiresAt = ticket.ExpiresAt.ToString("o"),
            };
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "create_file")]
    [Description("Create a new file with UTF-8 text content. Optionally place it in a parent folder. Returns the new file's id + metadata.")]
    public static async Task<object> CreateFile(
        DriveService drive,
        [Description("File name.")] string name,
        [Description("UTF-8 text content for the new file.")] string content,
        [Description("MIME type. Default text/plain.")] string mimeType = "text/plain",
        [Description("Optional parent folder id.")] string? folderId = null)
    {
        if (GdriveValidation.ValidateName(name) is { } nameErr) return Err(nameErr);
        if (GdriveValidation.ValidateContent(content) is { } contentErr) return Err(contentErr);
        if (GdriveValidation.ValidateMimeType(mimeType) is { } mimeErr) return Err(mimeErr);
        if (GdriveValidation.ValidateFolderId(folderId) is { } folderErr) return Err(folderErr);

        try
        {
            var meta = new DriveData.File { Name = name, MimeType = mimeType };
            if (!string.IsNullOrWhiteSpace(folderId)) meta.Parents = new List<string> { folderId };

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            var req = drive.Files.Create(meta, stream, mimeType);
            req.Fields = FileFields;
            var progress = await req.UploadAsync();
            if (progress.Exception is not null) return Err(progress.Exception.Message);
            return Summarize(req.ResponseBody);
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "update_file")]
    [Description("Update an existing file: rename it and/or replace its content. Provide newName to rename, newContent to replace the body (omit either to leave it unchanged).")]
    public static async Task<object> UpdateFile(
        DriveService drive,
        [Description("The Drive file id to update.")] string fileId,
        [Description("Optional new name. Omit to keep the current name.")] string? newName = null,
        [Description("Optional new UTF-8 text content. Omit to keep the current content.")] string? newContent = null,
        [Description("MIME type to set when replacing content. Default text/plain.")] string mimeType = "text/plain")
    {
        if (GdriveValidation.ValidateFileId(fileId) is { } idErr) return Err(idErr);
        if (GdriveValidation.ValidateOptionalNewName(newName) is { } nameErr) return Err(nameErr);
        if (GdriveValidation.ValidateOptionalNewContent(newContent) is { } contentErr) return Err(contentErr);
        if (GdriveValidation.ValidateMimeType(mimeType) is { } mimeErr) return Err(mimeErr);

        try
        {
            var meta = new DriveData.File();
            if (newName is not null) meta.Name = newName;

            if (newContent is not null)
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(newContent));
                var up = drive.Files.Update(meta, fileId, stream, mimeType);
                up.Fields = FileFields;
                var progress = await up.UploadAsync();
                if (progress.Exception is not null) return Err(progress.Exception.Message);
                return Summarize(up.ResponseBody);
            }

            var req = drive.Files.Update(meta, fileId);
            req.Fields = FileFields;
            return Summarize(await req.ExecuteAsync());
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "create_folder")]
    [Description("Create a new Drive folder (application/vnd.google-apps.folder). Optionally place it inside a parent folder. Returns the new folder's id + metadata.")]
    public static async Task<object> CreateFolder(
        DriveService drive,
        [Description("Folder name.")] string name,
        [Description("Optional parent folder id. Omit to create at Drive root.")] string? parentFolderId = null)
    {
        if (GdriveValidation.ValidateName(name) is { } nameErr) return Err(nameErr);
        if (GdriveValidation.ValidateFolderId(parentFolderId) is { } folderErr) return Err(folderErr);

        try
        {
            var meta = new DriveData.File { Name = name, MimeType = FolderMimeType };
            if (!string.IsNullOrWhiteSpace(parentFolderId)) meta.Parents = new List<string> { parentFolderId };

            var req = drive.Files.Create(meta);
            req.Fields = FileFields;
            return Summarize(await req.ExecuteAsync());
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "upload_file")]
    [Description("Upload BINARY content to Drive, losslessly: pass base64 in, it is decoded and uploaded verbatim. Use for non-text artifacts (audio clips, images, archives) that create_file (UTF-8 text only) cannot carry. Optionally place it in a parent folder. Returns the new file's id + metadata.")]
    public static async Task<object> UploadFile(
        DriveService drive,
        [Description("File name.")] string name,
        [Description("Base64-encoded file content (decoded byte-exact before upload).")] string contentBase64,
        [Description("MIME type of the decoded content, e.g. audio/mp4 or audio/wav.")] string mimeType,
        [Description("Optional parent folder id.")] string? folderId = null)
    {
        if (GdriveValidation.ValidateName(name) is { } nameErr) return Err(nameErr);
        if (GdriveValidation.ValidateBase64Content(contentBase64) is { } contentErr) return Err(contentErr);
        if (GdriveValidation.ValidateMimeType(mimeType) is { } mimeErr) return Err(mimeErr);
        if (GdriveValidation.ValidateFolderId(folderId) is { } folderErr) return Err(folderErr);

        try
        {
            var meta = new DriveData.File { Name = name, MimeType = mimeType };
            if (!string.IsNullOrWhiteSpace(folderId)) meta.Parents = new List<string> { folderId };

            var bytes = Convert.FromBase64String(contentBase64);
            using var stream = new MemoryStream(bytes);
            var req = drive.Files.Create(meta, stream, mimeType);
            req.Fields = FileFields;
            var progress = await req.UploadAsync();
            if (progress.Exception is not null) return Err(progress.Exception.Message);
            return Summarize(req.ResponseBody);
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "move_file")]
    [Description("Move a file between Drive folders via Files.Update AddParents/RemoveParents. Supply destFolderId to add a new parent and/or removeFolderId to detach an old one (at least one is required). Returns the file's updated metadata (including its new parents list).")]
    public static async Task<object> MoveFile(
        DriveService drive,
        [Description("The Drive file id to move.")] string fileId,
        [Description("Folder id to add as a new parent. Provide this and/or removeFolderId.")] string? destFolderId = null,
        [Description("Folder id to remove as a parent. Provide this and/or destFolderId.")] string? removeFolderId = null)
    {
        if (GdriveValidation.ValidateFileId(fileId) is { } idErr) return Err(idErr);
        if (string.IsNullOrWhiteSpace(destFolderId) && string.IsNullOrWhiteSpace(removeFolderId))
            return Err("at least one of destFolderId or removeFolderId is required");
        if (!string.IsNullOrWhiteSpace(destFolderId) &&
            GdriveValidation.ValidateRequiredParentId(destFolderId, "destFolderId") is { } destErr)
            return Err(destErr);
        if (!string.IsNullOrWhiteSpace(removeFolderId) &&
            GdriveValidation.ValidateRequiredParentId(removeFolderId, "removeFolderId") is { } remErr)
            return Err(remErr);

        try
        {
            var req = drive.Files.Update(new DriveData.File(), fileId);
            if (!string.IsNullOrWhiteSpace(destFolderId)) req.AddParents = destFolderId;
            if (!string.IsNullOrWhiteSpace(removeFolderId)) req.RemoveParents = removeFolderId;
            req.Fields = FileFields;
            return Summarize(await req.ExecuteAsync());
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [McpServerTool(Name = "delete_file")]
    [Description("Delete a file. By default moves it to the trash (recoverable); set permanent=true to delete it forever.")]
    public static async Task<object> DeleteFile(
        DriveService drive,
        [Description("The Drive file id.")] string fileId,
        [Description("Permanently delete instead of trashing. Default false (trash).")] bool permanent = false)
    {
        if (GdriveValidation.ValidateFileId(fileId) is { } idErr) return Err(idErr);

        try
        {
            if (permanent)
            {
                await drive.Files.Delete(fileId).ExecuteAsync();
                return new { fileId, deleted = "permanent" };
            }

            var req = drive.Files.Update(new DriveData.File { Trashed = true }, fileId);
            req.Fields = "id,name,trashed";
            var f = await req.ExecuteAsync();
            return new { fileId = f.Id, name = f.Name, deleted = "trashed", trashed = f.Trashed };
        }
        catch (Exception e) { return Err(e.Message); }
    }

    private static object Summarize(DriveData.File f) => new
    {
        id = f.Id,
        name = f.Name,
        mimeType = f.MimeType,
        size = f.Size,
        modifiedTime = f.ModifiedTimeRaw,
        createdTime = f.CreatedTimeRaw,
        parents = f.Parents,
        trashed = f.Trashed,
        webViewLink = f.WebViewLink,
        owners = f.Owners?.Select(o => o.EmailAddress),
    };

    // Drive 'q' string literals are single-quoted; escape embedded quotes/backslashes.
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
