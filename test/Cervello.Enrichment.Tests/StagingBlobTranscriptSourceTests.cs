using System.Security.Cryptography;
using System.Text;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Live <see cref="StagingBlobTranscriptSource"/> against a TEMP-DIR blob store laid out exactly as
/// the Watcher's <c>BlobStore</c> writes it (content-addressed <c>&lt;root&gt;/&lt;sha[..2]&gt;/&lt;sha&gt;.txt</c>).
/// Synthetic text only. Proves the RATIFIED base path: it reads the staged Google <c>.txt</c>
/// VERBATIM, and DEGRADES GRACEFULLY (returns null, never throws) when there is no Google sha / the
/// blob is absent / empty / unreadable — so a drain never hard-fails on the base.
/// </summary>
public sealed class StagingBlobTranscriptSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cervello-txt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string Stage(string text, string ext = ".txt")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var dir = Path.Combine(_root, sha[..2]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, sha + ext), bytes);
        return sha;
    }

    private static RecordingRef Rec(string? txtSha) =>
        new("20260704-standup", "audio-sha-aaa", "m4a", "fr", ready: true, googleTxtSha256: txtSha);

    [Fact]
    public async Task Reads_the_staged_google_txt_verbatim_as_the_base()
    {
        const string google = "Speaker 1: bonjour tout le monde\nSpeaker 2: salut";
        var sha = Stage(google);
        var src = new StagingBlobTranscriptSource(_root);

        var got = await src.GetGoogleBaseAsync(Rec(sha));

        Assert.NotNull(got);
        Assert.Equal(google, got!.Markdown); // VERBATIM — never paraphrased
        Assert.Equal("fr", got.Language);
    }

    [Fact]
    public async Task Strips_a_utf8_bom_but_keeps_the_text_verbatim()
    {
        const string google = "café résumé — verbatim";
        // Write WITH a BOM (Recorder exports sometimes carry one); the base must be the text, no BOM.
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(google)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(withBom)).ToLowerInvariant();
        var dir = Path.Combine(_root, sha[..2]);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, sha + ".txt"), withBom);
        var src = new StagingBlobTranscriptSource(_root);

        var got = await src.GetGoogleBaseAsync(Rec(sha));

        Assert.Equal(google, got!.Markdown);
    }

    [Fact]
    public async Task No_google_sha_degrades_gracefully_to_null()
    {
        var src = new StagingBlobTranscriptSource(_root);

        var got = await src.GetGoogleBaseAsync(Rec(txtSha: null));

        Assert.Null(got); // no Google .txt → no base (never throws, never fabricated)
    }

    [Fact]
    public async Task Absent_blob_degrades_gracefully_to_null()
    {
        var src = new StagingBlobTranscriptSource(_root);

        var got = await src.GetGoogleBaseAsync(Rec(new string('a', 64)));

        Assert.Null(got); // a recorded sha but no staged blob → graceful null, NOT an exception
    }

    [Fact]
    public async Task Empty_blob_degrades_gracefully_to_null_never_fabricated()
    {
        var sha = Stage(""); // empty .txt
        var src = new StagingBlobTranscriptSource(_root);

        var got = await src.GetGoogleBaseAsync(Rec(sha));

        Assert.Null(got);
    }

    [Fact]
    public void Refuses_a_staging_root_under_a_git_tree_custody_guard()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var staging = Path.Combine(repo, "staging");
        Directory.CreateDirectory(staging);

        Assert.Throws<InvalidOperationException>(() => new StagingBlobTranscriptSource(staging));
    }
}
