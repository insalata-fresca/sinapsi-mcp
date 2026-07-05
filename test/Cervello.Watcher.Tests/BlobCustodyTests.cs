using Cervello.Watcher.Ingest;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// recording-ingest — "Audio custody containment" (invariant 1). Audio bytes are
/// staged ONLY under the CT staging dir; a git working tree / repo path is refused.
/// </summary>
public sealed class BlobCustodyTests
{
    // ---- Scenario: Audio bytes never touch the repo ----
    [Fact]
    public void Staged_audio_lands_under_staging_and_not_under_a_git_tree()
    {
        using var ws = new TempWorkspace();
        var blobs = new BlobStore(ws.StagingDir);
        var audio = System.Text.Encoding.UTF8.GetBytes("audio-bytes-here");

        var (path, sha) = blobs.Write(audio, ".m4a");

        // Bytes are under the staging root...
        Assert.StartsWith(Path.GetFullPath(ws.StagingDir), Path.GetFullPath(path));
        Assert.True(File.Exists(path));
        Assert.Equal(audio, File.ReadAllBytes(path));
        // ...content-addressed by sha256, and no .git ancestor exists on that path.
        Assert.Contains(sha, path);
        foreach (var dir in Ancestors(path))
            Assert.False(Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")));
    }

    // ---- Guard: constructing a BlobStore under a git working tree THROWS ----
    [Fact]
    public void BlobStore_refuses_a_staging_dir_under_a_git_working_tree()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "cervello-gitrepo-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git")); // fake repo
            var stagingUnderRepo = Path.Combine(repoRoot, "recordings", "blobs");

            var ex = Assert.Throws<InvalidOperationException>(() => new BlobStore(stagingUnderRepo));
            Assert.Contains("custody", ex.Message);
            Assert.Contains(".git", ex.Message);
        }
        finally
        {
            if (Directory.Exists(repoRoot)) Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void BlobStore_refuses_a_staging_dir_directly_at_a_git_worktree_root()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "cervello-gitwt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(repoRoot);
            // git worktrees use a .git FILE (not a dir) — the guard must catch both.
            File.WriteAllText(Path.Combine(repoRoot, ".git"), "gitdir: /somewhere/.git/worktrees/x\n");

            Assert.Throws<InvalidOperationException>(() => new BlobStore(repoRoot));
        }
        finally
        {
            if (Directory.Exists(repoRoot)) Directory.Delete(repoRoot, recursive: true);
        }
    }

    // ---- Scenario: same content is a byte-identical re-write (content-addressed) ----
    [Fact]
    public void Rewriting_same_content_is_idempotent()
    {
        using var ws = new TempWorkspace();
        var blobs = new BlobStore(ws.StagingDir);
        var audio = System.Text.Encoding.UTF8.GetBytes("same");

        var (p1, s1) = blobs.Write(audio, ".m4a");
        var (p2, s2) = blobs.Write(audio, ".m4a");

        Assert.Equal(p1, p2);
        Assert.Equal(s1, s2);
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(path)!);
        for (var cur = d; cur is not null; cur = cur.Parent)
            yield return cur.FullName;
    }
}
