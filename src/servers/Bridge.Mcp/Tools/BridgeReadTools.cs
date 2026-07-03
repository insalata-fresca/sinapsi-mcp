using System.ComponentModel;
using System.Text;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;
using ModelContextProtocol.Server;


namespace Bridge.Mcp.Tools;

/// <summary>
/// B4b-3 READ TOOLS: list_workspaces, list_files, read_file, check_inbox.
///
/// All tools are read-only (no git writes). Auth scope: bridge:read:documents.
/// Rate bucket: read (60/min). Error contracts:
///   - read_file: ToolError(not_found) on 404.
///   - list_files: 404 → empty items list.
///   - check_inbox: 404 → empty items list; excludes non-file + .gitkeep entries.
///   - list_workspaces: returns {workspaces:[owner/repo]} in repo-search order.
/// </summary>
[McpServerToolType]
public sealed class BridgeReadTools(
    AuthService auth,
    GitOpsService git,
    AuditService audit,
    BridgeConfig config)
{
    // UTF-8 decoder that throws DecoderFallbackException on invalid bytes.
    // Python: raw.decode("utf-8") raises UnicodeDecodeError on non-UTF8.
    // C# Encoding.UTF8.GetString() silently replaces; UTF8Encoding(false, true) matches Python.
    private static readonly UTF8Encoding Utf8Strict =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ── list_workspaces ───────────────────────────────────────────────────────

    [McpServerTool(Name = "list_workspaces")]
    [Description(
        "List all Forgejo repos tagged with the 'knowledge' topic. " +
        "These are the repos the app side can deposit into and read from. " +
        "Returns {workspaces: [\"owner/repo\", ...]}.")]
    public async Task<object> ListWorkspaces(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.ReadDocumentsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            var workspaces = await git.ListReposWithTopicAsync("knowledge", cancellationToken);

            audit.Enqueue("list_workspaces", ctx, project: null, query: null);

            return new { workspaces };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── list_files ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_files")]
    [Description(
        "List files/directories under {repo}/{path}. " +
        "Returns {repo, path, items:[{path, type, size, sha}]}. " +
        "Returns empty items on 404 (directory does not exist). " +
        "A single-file response is wrapped in a list for uniform treatment.")]
    public async Task<object> ListFiles(
        [Description("Repo name (bare 'name' or 'owner/name').")] string repo,
        [Description("Sub-path to list. Empty string = repo root.")] string path = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.ReadDocumentsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            var repoFull = git.ResolveRepo(repo);
            var entries  = await git.ListContentsAsync(repoFull, path, cancellationToken);

            var items = entries.Select(e => new
            {
                path = e.Path,
                type = e.Type,   // "file" | "dir"
                size = e.Size,
                sha  = e.Sha,
            }).ToList();

            audit.Enqueue("list_files", ctx, project: repo, query: path);

            return new { repo = repoFull, path, items };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── read_file ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "read_file")]
    [Description(
        "Read a file under {repo}/. Returns {repo, path, content, encoding}. " +
        "encoding is 'utf-8' for text files; 'base64' for binary files. " +
        "Raises ToolError(not_found) on 404.")]
    public async Task<object> ReadFile(
        [Description("Repo name (bare 'name' or 'owner/name').")] string repo,
        [Description("File path within the repo.")] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.ReadDocumentsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            var repoFull = git.ResolveRepo(repo);

            byte[] raw;
            try
            {
                raw = await git.ReadFileBytesAsync(repoFull, path, cancellationToken);
            }
            catch (FileNotFoundException ex)
            {
                throw new BridgeToolException("not_found", ex.Message);
            }

            // Try UTF-8 decode; fall back to base64 for binary files.
            // Python: raw.decode("utf-8") raises UnicodeDecodeError on invalid bytes.
            // C# Encoding.UTF8.GetString() uses replacement fallback by default (no throw).
            // We must use UTF8Encoding(false, throwOnInvalidBytes:true) to match Python.
            string content;
            string encoding;
            try
            {
                content  = Utf8Strict.GetString(raw);
                encoding = "utf-8";
            }
            catch (DecoderFallbackException)
            {
                content  = Convert.ToBase64String(raw);
                encoding = "base64";
            }

            audit.Enqueue("read_file", ctx, project: repo, query: path);

            return new { repo = repoFull, path, content, encoding };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── check_inbox ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "check_inbox")]
    [Description(
        "List files under {repo}/inbox/from-code/. " +
        "Excludes non-file entries and .gitkeep. " +
        "Returns {repo, items:[{path, size, sha}]}. " +
        "Returns empty items list on 404 (inbox does not exist).")]
    public async Task<object> CheckInbox(
        [Description("Repo name (bare 'name' or 'owner/name').")] string repo,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.ReadDocumentsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            var repoFull = git.ResolveRepo(repo);
            var entries  = await git.ListContentsAsync(repoFull, "inbox/from-code", cancellationToken);

            // Filter: keep only type=file entries that are NOT .gitkeep.
            var items = entries
                .Where(e => e.Type == "file" && !e.Path.EndsWith(".gitkeep", StringComparison.Ordinal))
                .Select(e => new
                {
                    path = e.Path,
                    size = e.Size,
                    sha  = e.Sha,
                })
                .ToList();

            audit.Enqueue("check_inbox", ctx, project: repo);

            return new { repo = repoFull, items };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }
}
