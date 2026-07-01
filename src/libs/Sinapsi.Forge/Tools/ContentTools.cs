using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Tools;

/// <summary>Common contents/files tools — including the byte-safe atomic <c>commit_files</c>.
/// File writes accept content EITHER as raw UTF-8 text (<c>content</c>) — the server
/// base64-encodes it — OR as <c>content_base64</c> for exact/binary bytes. The raw path
/// exists so an execution session with no shell doesn't have to hand-encode base64.</summary>
[McpServerToolType]
public sealed class ContentTools
{
    /// <summary>Resolve a write payload to base64: raw <paramref name="content"/> (UTF-8)
    /// wins when supplied (incl. empty string for an empty file); else
    /// <paramref name="contentBase64"/> is used verbatim. Exactly one must be present.</summary>
    public static string ResolveBase64(string? content, string? contentBase64)
    {
        if (content is not null && contentBase64 is not null)
            throw new ArgumentException("provide EITHER 'content' (raw text) OR 'content_base64', not both");
        if (content is not null) return Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        if (contentBase64 is not null) return contentBase64;
        throw new ArgumentException("provide 'content' (raw UTF-8 text) or 'content_base64' (exact bytes)");
    }

    /// <summary>Normalise a commit_files batch so the client only ever sees base64: any
    /// create/update file given as raw <see cref="ForgeFileChange.Content"/> is encoded
    /// into <see cref="ForgeFileChange.ContentBase64"/> here. delete files pass through.</summary>
    public static IReadOnlyList<ForgeFileChange> NormalizeFiles(IReadOnlyList<ForgeFileChange> files)
    {
        var outp = new List<ForgeFileChange>(files.Count);
        foreach (var f in files)
        {
            if (f.Content is null) { outp.Add(f); continue; }
            if (f.ContentBase64 is not null)
                throw new ArgumentException($"file '{f.Path}': provide EITHER 'content' OR 'content_base64', not both");
            outp.Add(f with
            {
                ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Content)),
                Content = null,
            });
        }
        return outp;
    }

    [McpServerTool(Name = "get_file", ReadOnly = true)]
    [Description("Get a file's content (base64) or a directory listing at an optional ref (branch/tag/sha).")]
    public static Task<object> GetFile(
        IForgeClient forge, string owner, string repo,
        [Description("Path in the repo.")] string path,
        [Description("Branch/tag/sha. Omit for the default branch.")] string? gitRef = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePath(path)
                  ?? SinapsiForgeValidation.ValidateRef(gitRef, "gitRef", required: false),
            async () => await forge.GetContentsAsync(owner, repo, path, gitRef, ct));

    [McpServerTool(Name = "get_file_binary", ReadOnly = true)]
    [Description("Get a file's RAW bytes as base64 (for binary/non-UTF-8 files), at an optional ref.")]
    public static Task<object> GetFileBinary(
        IForgeClient forge, string owner, string repo,
        [Description("Path in the repo.")] string path,
        [Description("Branch/tag/sha. Omit for the default branch.")] string? gitRef = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePath(path)
                  ?? SinapsiForgeValidation.ValidateRef(gitRef, "gitRef", required: false),
            async () => await forge.GetFileBinaryAsync(owner, repo, path, gitRef, ct));

    [McpServerTool(Name = "create_or_update_file", Destructive = false)]
    [Description("Create or update a single file. Provide the content as `content` (RAW UTF-8 text — easiest; the server base64-encodes it) OR `content_base64` (exact/binary bytes) — exactly one. To UPDATE pass the current file sha; omit sha to CREATE.")]
    public static Task<object> CreateOrUpdateFile(
        IForgeClient forge, string owner, string repo,
        [Description("Path in the repo.")] string path,
        [Description("Commit message.")] string message,
        [Description("Branch to commit to.")] string branch,
        [Description("File content as RAW UTF-8 text — the server base64-encodes it. Use this for text files. Mutually exclusive with content_base64.")] string? content = null,
        [Description("File content, base64-encoded (exact bytes) — for binary/non-UTF-8 files. Mutually exclusive with content.")] string? content_base64 = null,
        [Description("Current file sha — REQUIRED to update; omit to create.")] string? sha = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePath(path)
                  ?? SinapsiForgeValidation.ValidateRef(branch, "branch"),
            async () => await forge.CreateOrUpdateFileAsync(owner, repo, path, ResolveBase64(content, content_base64), message, branch, sha, ct));

    [McpServerTool(Name = "delete_file", Destructive = false)]
    [Description("Delete a file. Requires the current file sha.")]
    public static Task<object> DeleteFile(
        IForgeClient forge, string owner, string repo,
        [Description("Path in the repo.")] string path,
        [Description("Commit message.")] string message,
        [Description("Branch to commit to.")] string branch,
        [Description("Current file sha.")] string sha,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePath(path)
                  ?? SinapsiForgeValidation.ValidateRef(branch, "branch"),
            async () => await forge.DeleteFileAsync(owner, repo, path, message, branch, sha, ct));

    [McpServerTool(Name = "commit_files", Destructive = false)]
    [Description("Atomic, byte-safe, multi-file commit. Each file: {path, operation: create|update|delete, content (RAW UTF-8 text, easiest) OR content_base64 (exact/binary bytes) for create/update — exactly one, sha (for update/delete), from_path (rename)}. One commit on `branch` (optionally creating `new_branch` from it first). Binary + whitespace-sensitive files are byte-perfect.")]
    public static Task<object> CommitFiles(
        IForgeClient forge, string owner, string repo,
        [Description("Branch to commit to (or to base new_branch on).")] string branch,
        [Description("Commit message.")] string message,
        [Description("Files to change in one commit.")] ForgeFileChange[] files,
        [Description("If set, create this branch from `branch` and commit there.")] string? new_branch = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateRef(branch, "branch")
                  ?? SinapsiForgeValidation.ValidateRef(new_branch, "new_branch", required: false),
            async () => await forge.CommitFilesAsync(owner, repo, branch, new_branch, message, NormalizeFiles(files), ct));
}
