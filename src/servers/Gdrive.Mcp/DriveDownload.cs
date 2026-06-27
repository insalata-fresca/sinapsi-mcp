using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gdrive.Mcp;

/// <summary>
/// Lossless Drive media-download plumbing shared by the binary tools, via the official
/// <c>Google.Apis.Drive.v3</c> client's own download surface on
/// <see cref="FilesResource.GetRequest"/> — <c>DownloadRangeAsync</c> (byte range, built on the
/// client's <c>MediaDownloader</c>) and <c>DownloadAsync</c> (full stream). This is the idiomatic
/// path: the client builds the <c>alt=media</c> URL, applies the credential (bearer + auto-refresh)
/// and its retry policy — we do NOT hand-construct the REST URL or drive a raw HttpClient.
/// </summary>
public static class DriveMedia
{
    /// <summary>
    /// Download <paramref name="count"/> bytes starting at <paramref name="offset"/> via the client's
    /// ranged downloader (HTTP <c>Range</c> under the hood). Returns just those bytes.
    /// </summary>
    public static async Task<byte[]> FetchRangeAsync(
        DriveService drive, string fileId, long offset, long count, CancellationToken ct)
    {
        var req = drive.Files.Get(fileId);
        req.SupportsAllDrives = true; // shared-drive ids resolve

        using var ms = new MemoryStream();
        var progress = await req.DownloadRangeAsync(ms, new RangeHeaderValue(offset, offset + count - 1), ct);
        if (progress.Status == DownloadStatus.Failed)
            throw progress.Exception ?? new InvalidOperationException("Drive range download failed");
        return ms.ToArray();
    }
}

/// <summary>One issued download ticket: a random token mapping to a Drive file id for a short TTL.</summary>
public sealed record DownloadTicket(string Token, string FileId, string? Name, long? Size, DateTimeOffset ExpiresAt);

/// <summary>
/// In-memory registry of short-lived download tickets backing <c>download_to_url</c>. A ticket is
/// a 128-bit random token (an unguessable capability URL, the signed-URL pattern) good for a
/// bounded window; the file bytes themselves are never held — the serving endpoint streams Drive
/// on demand. Process-local + ephemeral by design: tickets do not survive a restart (re-issue).
/// </summary>
public sealed class DownloadTicketStore
{
    private readonly ConcurrentDictionary<string, DownloadTicket> _tickets = new();

    public DownloadTicket Issue(string fileId, string? name, long? size, TimeSpan ttl)
    {
        Sweep();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var ticket = new DownloadTicket(token, fileId, name, size, DateTimeOffset.UtcNow + ttl);
        _tickets[token] = ticket;
        return ticket;
    }

    public bool TryGet(string token, out DownloadTicket ticket)
    {
        if (_tickets.TryGetValue(token, out var t) && t.ExpiresAt > DateTimeOffset.UtcNow)
        {
            ticket = t;
            return true;
        }
        _tickets.TryRemove(token, out _);
        ticket = null!;
        return false;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _tickets)
            if (kv.Value.ExpiresAt <= now) _tickets.TryRemove(kv.Key, out _);
    }
}

/// <summary>
/// Maps the unauthenticated streaming endpoint <c>GET /gdrive-dl/{token}</c> on the MCP host. It
/// resolves a live ticket and streams the Drive file to the response via the client's
/// <see cref="FilesResource.GetRequest"/> <c>DownloadAsync</c> (MediaDownloader chunks internally,
/// so memory stays bounded regardless of file size — the right primitive for tens-of-MB artifacts).
/// Reachable at the configured download base URL; sits on the host bind, outside <c>/mcp</c>, so the
/// random short-lived token is the access control.
/// </summary>
public static class DriveDownloadEndpoints
{
    public static void MapDownloadEndpoint(this WebApplication app)
    {
        app.MapGet("/gdrive-dl/{token}", async (
            string token,
            DownloadTicketStore store,
            DriveService drive,
            ILoggerFactory loggerFactory,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("gdrive-dl");
            if (!store.TryGet(token, out var ticket))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("invalid or expired download token", ct);
                return;
            }

            ctx.Response.ContentType = "application/octet-stream";
            if (ticket.Size is long len) ctx.Response.ContentLength = len; // authoritative from metadata at issue time
            var fname = SanitizeFilename(string.IsNullOrWhiteSpace(ticket.Name) ? ticket.FileId : ticket.Name!);
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fname}\"";

            var req = drive.Files.Get(ticket.FileId);
            req.SupportsAllDrives = true;
            var progress = await req.DownloadAsync(ctx.Response.Body, ct);
            if (progress.Status == DownloadStatus.Failed)
                // Headers/body may already be in flight; the broken stream signals failure to the client.
                log.LogWarning(progress.Exception, "stream of {FileId} failed after {Bytes} bytes",
                    ticket.FileId, progress.BytesDownloaded);
        });
    }

    // Strip anything that could break the Content-Disposition header or path-traverse a wget -O default.
    private static string SanitizeFilename(string name) =>
        new(name.Select(c => c is '"' or '\\' or '/' or '\r' or '\n' ? '_' : c).ToArray());
}
