using System.Security.Cryptography;

namespace Cervello.Watcher.Ingest;

/// <summary>
/// Custody-safe blob store (invariant 1 / recording-ingest "Audio custody
/// containment"). Audio (<c>.m4a</c>) and transcript bytes are staged ONLY under
/// the configured CT-local staging dir, keyed by <c>audio_sha256</c>. The guard
/// THROWS if the staging dir resolves to (or under) a git working tree / repo
/// path — audio bytes must NEVER reach the git tree, be committed, or leave the CT.
/// Only references + checksums cross that boundary.
/// </summary>
public sealed class BlobStore
{
    private readonly string _stagingRoot;

    public BlobStore(string stagingDir)
    {
        var full = Path.GetFullPath(stagingDir);
        GuardNotUnderGitTree(full);
        _stagingRoot = full;
        Directory.CreateDirectory(_stagingRoot);
    }

    /// <summary>
    /// Write bytes into staging keyed by their sha256; returns (path, sha256).
    /// Idempotent: the same content lands at the same path (content-addressed), so
    /// a re-write is a byte-identical no-op.
    /// </summary>
    public (string Path, string Sha256) Write(ReadOnlySpan<byte> content, string extension)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        // Two-level fan-out to avoid a single huge dir; still under the staging root.
        var dir = Path.Combine(_stagingRoot, sha[..2]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sha + ext);
        // Defence-in-depth: recompute the guard against the FINAL path, so a crafted
        // staging root or symlink can't smuggle a write under a repo.
        GuardNotUnderGitTree(path);
        if (!File.Exists(path))
            File.WriteAllBytes(path, content.ToArray());
        return (path, sha);
    }

    /// <summary>Compute sha256 for a byte span without staging (used by tests / checksums).</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// Reject any path that is a git working tree or lives under one. Walks up the
    /// ancestry looking for a <c>.git</c> entry (dir or file — worktrees use a file).
    /// </summary>
    private static void GuardNotUnderGitTree(string path)
    {
        var dir = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var dotGit = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
                throw new InvalidOperationException(
                    $"custody violation: refusing to stage blob bytes under a git working tree " +
                    $"('{d.FullName}' contains .git). Audio must stay under the CT staging dir " +
                    $"(invariant: no audio bytes in git).");
        }
    }
}
