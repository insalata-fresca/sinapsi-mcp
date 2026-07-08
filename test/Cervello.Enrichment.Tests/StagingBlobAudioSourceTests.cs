using System.Security.Cryptography;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Live <see cref="StagingBlobAudioSource"/> against a TEMP-DIR blob store laid out exactly as the
/// Watcher's <c>BlobStore</c> writes it (content-addressed <c>&lt;root&gt;/&lt;sha[..2]&gt;/&lt;sha&gt;.m4a</c>).
/// Synthetic bytes only (no personal audio). Proves: it fetches the transient bytes, honours the
/// two-level fan-out + extension fallbacks, and maps an absent / empty / unreadable blob to the
/// terminal <see cref="AudioUnavailableException"/> (never fabricates audio) — plus the custody guard.
/// </summary>
public sealed class StagingBlobAudioSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cervello-audio-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private (string sha, byte[] bytes) Stage(byte[] bytes, string ext = ".m4a")
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var dir = Path.Combine(_root, sha[..2]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, sha + ext), bytes);
        return (sha, bytes);
    }

    [Fact]
    public async Task Fetches_the_transient_bytes_from_the_content_addressed_blob()
    {
        var (sha, bytes) = Stage([1, 2, 3, 4, 5]);
        var src = new StagingBlobAudioSource(_root);

        var got = await src.FetchAsync("20260704-standup", sha);

        Assert.Equal(bytes, got.ToArray());
    }

    [Fact]
    public async Task Resolves_a_sha_recorded_uppercase()
    {
        var (sha, bytes) = Stage([9, 8, 7]);
        var src = new StagingBlobAudioSource(_root);

        var got = await src.FetchAsync("rec", sha.ToUpperInvariant());

        Assert.Equal(bytes, got.ToArray());
    }

    [Fact]
    public async Task Falls_back_across_the_staged_audio_extensions()
    {
        var (sha, bytes) = Stage([4, 4, 4], ext: ".wav"); // Watcher re-container / non-default
        var src = new StagingBlobAudioSource(_root);

        var got = await src.FetchAsync("rec", sha);

        Assert.Equal(bytes, got.ToArray());
    }

    [Fact]
    public async Task Absent_blob_is_a_terminal_contract_violation()
    {
        var src = new StagingBlobAudioSource(_root);

        await Assert.ThrowsAsync<AudioUnavailableException>(
            () => src.FetchAsync("rec", new string('a', 64)));
    }

    [Fact]
    public async Task Empty_blob_is_terminal_no_fabrication()
    {
        var (sha, _) = Stage(Array.Empty<byte>());
        var src = new StagingBlobAudioSource(_root);

        await Assert.ThrowsAsync<AudioUnavailableException>(() => src.FetchAsync("rec", sha));
    }

    [Fact]
    public async Task Malformed_sha_is_terminal_not_a_crash()
    {
        var src = new StagingBlobAudioSource(_root);

        await Assert.ThrowsAsync<AudioUnavailableException>(() => src.FetchAsync("rec", "x"));
    }

    [Fact]
    public void Refuses_a_staging_root_under_a_git_tree_custody_guard()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var staging = Path.Combine(repo, "staging");
        Directory.CreateDirectory(staging);

        Assert.Throws<InvalidOperationException>(() => new StagingBlobAudioSource(staging));
    }

    [Fact]
    public async Task Empty_id_or_sha_is_rejected()
    {
        var src = new StagingBlobAudioSource(_root);
        await Assert.ThrowsAsync<ArgumentException>(() => src.FetchAsync("", "abcd"));
        await Assert.ThrowsAsync<ArgumentException>(() => src.FetchAsync("rec", ""));
    }
}
