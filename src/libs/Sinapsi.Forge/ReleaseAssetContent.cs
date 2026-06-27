using System.Net.Http.Headers;

namespace Sinapsi.Forge;

/// <summary>
/// Resolves a release-asset payload from EXACTLY ONE of three sources into an
/// <see cref="HttpContent"/> ready to POST as the multipart attachment:
/// <list type="bullet">
///   <item><c>sourcePath</c> — a file on the MCP host's filesystem, streamed as
///   <see cref="StreamContent"/> so a 20MB+ binary is not fully buffered in memory.</item>
///   <item><c>sourceUrl</c> — an http(s) URL fetched + streamed via a plain
///   <see cref="HttpClient"/> (NOT the forge-authenticated client — the URL is arbitrary).</item>
///   <item><c>contentBase64</c> — inline base64, decoded to a byte array (small files only).</item>
/// </list>
/// Throws <see cref="ArgumentException"/> if not exactly one source is supplied.
/// The returned <c>dispose</c> bundles any owned disposables (file stream, the URL
/// <see cref="HttpClient"/> + its response) for the caller to dispose after the POST.
/// </summary>
public static class ReleaseAssetContent
{
    public static async Task<(HttpContent content, IDisposable? dispose)> ResolveAsync(
        string? contentBase64, string? sourcePath, string? sourceUrl, CancellationToken ct)
    {
        var provided = new[] { contentBase64, sourcePath, sourceUrl }.Count(s => !string.IsNullOrWhiteSpace(s));
        if (provided != 1)
            throw new ArgumentException(
                "upload_release_asset requires EXACTLY ONE of content_base64, source_path, or source_url.");

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"source_path not found on the MCP host: {sourcePath}");
            var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            return (OctetStream(new StreamContent(fs)), fs);
        }

        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            // Plain client — never the forge-authenticated HttpClient (the URL is arbitrary).
            var client = new HttpClient();
            HttpResponseMessage? resp = null;
            try
            {
                resp = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new ForgeApiException((int)resp.StatusCode,
                        $"{(int)resp.StatusCode} {resp.ReasonPhrase} fetching source_url {sourceUrl}");
                var stream = await resp.Content.ReadAsStreamAsync(ct);
                var bundle = new DisposeBundle(resp, client);
                resp = null; client = null!; // ownership transferred to the bundle
                return (OctetStream(new StreamContent(stream)), bundle);
            }
            finally
            {
                resp?.Dispose();
                client?.Dispose();
            }
        }

        var bytes = Convert.FromBase64String(contentBase64!);
        return (OctetStream(new ByteArrayContent(bytes)), null);
    }

    private static HttpContent OctetStream(HttpContent c)
    {
        c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return c;
    }

    private sealed class DisposeBundle(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var i in items) i.Dispose();
        }
    }
}
