using System.Text;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IBaseTranscriptSource"/> — reads a recording's paired Google <c>.txt</c>
/// transcript (the RATIFIED base) from the CT-local staging blob store the <c>Cervello.Watcher</c>
/// writes (its <c>BlobStore</c>: content-addressed by the file's sha256 under
/// <c>&lt;stagingRoot&gt;/&lt;sha[..2]&gt;/&lt;sha&gt;.txt</c>). The staged <c>.txt</c> content sha is
/// carried on <see cref="RecordingRef.GoogleTxtSha256"/> (resolved by the drain from the Watcher's
/// download ledger). The returned text is the Google transcript VERBATIM (never paraphrased or
/// summarised) wrapped in a <see cref="BaseTranscript"/> in the recording's language.
///
/// <para><b>Graceful degrade (never a hard failure).</b> A recording with no Google <c>.txt</c>
/// (null sha), or a staged blob that is absent / unreadable / empty, yields <see langword="null"/> —
/// "no Google base present" — NOT an exception. The base-transcribe stage then decides how to proceed
/// (optional CT126 fallback if enabled, else no base). This is deliberate: a full drain of a real
/// recording (which carries a Google transcript) must never hard-fail on a transient staging glitch,
/// and CT126 is never a hard dependency of the base path.</para>
///
/// <para><b>Confinement.</b> The transcript bytes are read from the on-CT staging dir and returned
/// transiently to the caller (which persists only the derived base markdown git-side). The staging
/// root is verified NOT to be under a git working tree at construction (defence-in-depth), the same
/// custody guard the audio source applies.</para>
/// </summary>
public sealed class StagingBlobTranscriptSource : IBaseTranscriptSource
{
    /// <summary>The transcript containers the Watcher may have staged (<c>.txt</c> is the Recorder default).</summary>
    private static readonly string[] TranscriptExtensions = [".txt", ""];

    private readonly string _stagingRoot;
    private readonly ILogger _log;

    public StagingBlobTranscriptSource(string stagingRoot, ILogger<StagingBlobTranscriptSource>? log = null)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
            throw new ArgumentException("stagingRoot must be non-empty", nameof(stagingRoot));
        var full = Path.GetFullPath(stagingRoot);
        GuardNotUnderGitTree(full);
        _stagingRoot = full;
        _log = log ?? NullLogger<StagingBlobTranscriptSource>.Instance;
    }

    public async Task<BaseTranscript?> GetGoogleBaseAsync(RecordingRef recording, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var sha = recording.GoogleTxtSha256;
        if (string.IsNullOrWhiteSpace(sha) || sha.Length < 2)
        {
            // No paired Google .txt for this recording — a normal, gracefully-handled case.
            _log.LogInformation("google-base {Id}: no staged Google .txt (no sha) — degrading gracefully", recording.Id);
            return null;
        }

        sha = sha.ToLowerInvariant();
        var dir = Path.Combine(_stagingRoot, sha[..2]);

        string? found = null;
        foreach (var ext in TranscriptExtensions)
        {
            var candidate = Path.Combine(dir, sha + ext);
            if (File.Exists(candidate)) { found = candidate; break; }
        }

        if (found is null)
        {
            _log.LogWarning("google-base {Id}: staged .txt blob {Sha} absent under '{Dir}' — degrading gracefully (no base)",
                recording.Id, sha, dir);
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(found, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "google-base {Id}: staged .txt blob {Sha} unreadable — degrading gracefully (no base)",
                recording.Id, sha);
            return null;
        }

        if (bytes.Length == 0)
        {
            _log.LogWarning("google-base {Id}: staged .txt blob {Sha} is empty — degrading gracefully (no base)",
                recording.Id, sha);
            return null;
        }

        // Verbatim Google text (UTF-8, BOM-tolerant). Never summarised, never fabricated.
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes).TrimStart('﻿');
        _log.LogInformation("google-base {Id}: read staged Google .txt {Sha} ({Bytes} bytes) — verbatim base",
            recording.Id, sha, bytes.Length);
        return new BaseTranscript(text, recording.Language);
    }

    /// <summary>
    /// Reject a staging root that is a git working tree or lives under one (the same custody guard the
    /// audio source + the Watcher's <c>BlobStore</c> apply) — staged bytes must never touch git.
    /// </summary>
    private static void GuardNotUnderGitTree(string path)
    {
        var dir = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var dotGit = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
                throw new InvalidOperationException(
                    $"custody violation: refusing to read staged transcript bytes from under a git working " +
                    $"tree ('{d.FullName}' contains .git). The staging dir must be a CT-local path outside any repo.");
        }
    }
}
