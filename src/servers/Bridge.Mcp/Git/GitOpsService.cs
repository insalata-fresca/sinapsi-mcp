using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bridge.Mcp.Auth;
using Sinapsi.Forge;
using Sinapsi.Forge.Gitea;
using Sinapsi.Forge.Model;

namespace Bridge.Mcp.Git;

/// <summary>
/// Bridge-specific git / Forgejo operations.
///
/// Uses <see cref="IForgeClient"/> (GiteaForgeClient) for REST-based ops
/// (list contents, create-or-update file, commit files, add topic, list commits,
/// search repos) and local shallow git clones for grep-based search.
///
/// The clone cache lives at BRIDGE_REPO_CACHE/owner/repo. A per-repo
/// <see cref="SemaphoreSlim"/> serializes writers (mirrors Python threading.Lock).
///
/// Commit pipeline (commit_to_repo): clone --depth 50 or fast-forward,
/// write file(s), git add -A, git commit, git push with 3-retry rebase —
/// byte-faithful port of git_ops.commit_to_repo in the Python source.
/// </summary>
public sealed class GitOpsService(
    IForgeClient forge,
    BridgeConfig config,
    IHttpClientFactory httpClientFactory,
    ILogger<GitOpsService> logger)
{
    // Per-repo write locks (SemaphoreSlim(1,1) mirrors Python threading.Lock).
    private readonly Dictionary<string, SemaphoreSlim> _repoLocks = new();
    private readonly SemaphoreSlim _locksGuard = new(1, 1);

    // ── Slug + sha8 (byte-parity with Python) ────────────────────────────────

    private static readonly Regex SlugRe = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Filesystem-safe slug used in deposit filenames.
    /// Port of Python: _SLUG_RE.sub("-", text.lower()).strip("-")[:64] or "untitled"
    ///
    /// Python str.lower() differs from C# ToLowerInvariant() for two non-ASCII characters
    /// whose Unicode full case-folding produces a leading ASCII letter:
    ///   U+0130 İ (LATIN CAPITAL LETTER I WITH DOT ABOVE) → "i̇" in Python, unchanged in C#
    ///   U+212A K (KELVIN SIGN) → "k" in Python, unchanged in C#
    /// Pre-substituting these before ToLowerInvariant() achieves byte-exact parity with Python,
    /// so deposit filenames are identical regardless of which bridge generated them.
    /// </summary>
    public static string Slugify(string text)
    {
        // Pre-substitute characters where Python str.lower() diverges from ToLowerInvariant()
        // so that deposit filenames produced by C# are byte-identical to the Python bridge.
        //
        // Python str.lower() performs Unicode full case-folding; the only two non-ASCII BMP
        // characters whose full-folding produces a sequence starting with an ASCII [a-z] letter are:
        //   U+0130 İ (LATIN CAPITAL LETTER I WITH DOT ABOVE)
        //       Python: "i" + U+0307 (COMBINING DOT ABOVE) -- combining dot is non-[a-z0-9],
        //               so the regex later yields a "-" separator, e.g. "İstanbul" -> "i-stanbul"
        //   U+212A K (KELVIN SIGN) -> "k"
        // ToLowerInvariant() leaves both unchanged (invariant lowercasing is simple, not full).
        // Replacing them first makes the regex filter produce the same result as Python.
        text = text
            .Replace("İ", "i̇") // U+0130 -> i + COMBINING DOT ABOVE (matches Python)
            .Replace("K", "k");       // U+212A Kelvin Sign -> k
        var s = SlugRe.Replace(text.ToLowerInvariant(), "-").Trim('-');
        if (s.Length > 64) s = s[..64];
        return s.Length == 0 ? "untitled" : s;
    }

    /// <summary>
    /// SHA-256 of the UTF-8 content, first 8 hex chars.
    /// Port of Python: hashlib.sha256(text.encode()).hexdigest()[:8]
    /// </summary>
    public static string Sha8(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant()[..8];
    }

    /// <summary>
    /// Reject path traversal and absolute paths in deposit_artifact.
    /// Port of Python: _safe_artifact_path
    /// </summary>
    public static string SafeArtifactPath(string p)
    {
        p = p.Trim().TrimStart('/');
        if (string.IsNullOrEmpty(p))
            throw new BridgeToolException("invalid_path", "path must not be empty");
        if (p.Split('/').Contains(".."))
            throw new BridgeToolException("invalid_path", "path traversal disallowed");
        if (p.Length > 256)
            throw new BridgeToolException("invalid_path", "path too long");
        return p;
    }

    // ── Repo qualification ─────────────────────────────────────────────────────

    private static readonly Regex RepoNameRe = new(@"^[a-zA-Z0-9_][a-zA-Z0-9._\-]{0,99}$", RegexOptions.Compiled);

    /// <summary>Validate and qualify a bare repo name to owner/repo.</summary>
    public string ResolveRepo(string repo)
    {
        var name = repo.Trim();
        if (name.Contains('/')) return name;
        if (!RepoNameRe.IsMatch(name))
            throw new BridgeToolException("invalid_repo", $"repo name must match {RepoNameRe}");
        return $"{config.ForgejoUser}/{name}";
    }

    public string ArchiveRepo() => $"{config.ForgejoUser}/personal-archive";
    public string FactsRepo()   => $"{config.ForgejoUser}/personal-facts";

    public string FileUrl(string repoFull, string path)
        => $"{config.ForgejoBaseUrl.TrimEnd('/')}/{repoFull}/src/branch/main/{path}";

    // ── Repo existence + auto-create (Python: api_repo_exists / api_create_repo) ─────────────

    /// <summary>
    /// Returns true when the repo exists (HTTP 200 from Forgejo /repos/{owner}/{repo}).
    /// Port of Python: api_repo_exists(repo_full) → resp.status_code == 200.
    /// </summary>
    public async Task<bool> ApiRepoExistsAsync(string repoFull, CancellationToken ct)
    {
        var (owner, repo) = Split(repoFull);
        try
        {
            await forge.GetRepoAsync(owner, repo, ct);
            return true;
        }
        catch (ForgeApiException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>
    /// Auto-create a private repo under the configured user. No-op if it already exists.
    /// Port of Python: api_create_repo(name, private=True, description='bridge-mcp managed').
    /// </summary>
    public async Task ApiCreateRepoAsync(string name, CancellationToken ct)
    {
        var repoFull = $"{config.ForgejoUser}/{name}";
        if (await ApiRepoExistsAsync(repoFull, ct))
            return;
        await forge.CreateRepoAsync(
            new CreateRepoRequest(
                Name: name,
                Description: "bridge-mcp managed",
                Private: true,
                AutoInit: true,
                DefaultBranch: "main"),
            ct);
    }

    // ── REST passthrough (no clone needed) ────────────────────────────────────

    /// <summary>List directory contents (or empty on 404). Wraps single-file dict in list.</summary>
    public async Task<IReadOnlyList<ForgeDirEntry>> ListContentsAsync(
        string repoFull, string path, CancellationToken ct)
    {
        var (owner, repo) = Split(repoFull);
        try
        {
            var listing = await forge.GetContentsAsync(owner, repo, path, null, ct);
            if (listing.Type == "dir")
                return listing.Entries ?? [];
            // Single file — wrap uniformly (matches Python api_list_contents single-file-wrap).
            if (listing.File is { } f)
                return [new ForgeDirEntry(f.Path, f.Path, "file", f.Size, f.Sha)];
            return [];
        }
        catch (ForgeApiException ex) when (ex.Status == 404)
        {
            return [];
        }
    }

    /// <summary>
    /// Read a file as raw bytes via the Forgejo /raw/{path}?ref=HEAD endpoint.
    ///
    /// Python parity (item 5): Python api_read_file_bytes hits /api/v1/repos/{repo}/raw/{path}?ref=HEAD.
    /// The previous C# implementation used /media (GetFileBinaryAsync), which for LFS-tracked files
    /// returns the resolved blob — different bytes than /raw which returns the pointer text.
    /// For non-LFS files both return the same bytes, but /raw is the correct endpoint for parity.
    /// The UTF-8-else-base64 fallback in BridgeReadTools.ReadFile is preserved unchanged.
    /// </summary>
    public async Task<byte[]> ReadFileBytesAsync(string repoFull, string path, CancellationToken ct)
    {
        // Call /api/v1/repos/{owner}/{repo}/raw/{path}?ref=HEAD directly —
        // mirrors Python git_ops.api_read_file_bytes: _api_url(repo_full, f"raw/{path.lstrip('/')}")
        var escapedPath = string.Join("/", path.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
        var endpoint    = $"repos/{Uri.EscapeDataString(Split(repoFull).owner)}/{Uri.EscapeDataString(Split(repoFull).repo)}/raw/{escapedPath}?ref=HEAD";
        try
        {
            var raw = await GetForgeJsonBytesAsync(endpoint, ct);
            return raw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"{repoFull}:{path} not found");
        }
        catch (ForgeApiException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException($"{repoFull}:{path} not found");
        }
    }

    /// <summary>
    /// Raw GET against the Forgejo API, returning the response body as bytes (not JSON).
    /// Used for /raw/{path} file reads where the response is the file content, not JSON.
    /// </summary>
    private async Task<byte[]> GetForgeJsonBytesAsync(string path, CancellationToken ct)
    {
        using var http2 = httpClientFactory.CreateClient("forge-raw");
        using var resp  = await http2.GetAsync(path, ct);
        if ((int)resp.StatusCode == 404)
            throw new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Read a file as raw UTF-8 text (throws FileNotFoundException on 404).</summary>
    public async Task<string> ReadFileRawAsync(string repoFull, string path, CancellationToken ct)
    {
        var bytes = await ReadFileBytesAsync(repoFull, path, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Add a topic to a Forgejo repo (idempotent).</summary>
    public async Task AddRepoTopicAsync(string repoFull, string topic, CancellationToken ct)
    {
        var (owner, repo) = Split(repoFull);
        try { await forge.AddRepoTopicAsync(owner, repo, topic, ct); }
        catch (Exception ex)
        {
            logger.LogWarning("AddRepoTopic {Repo} failed: {Message}", repoFull, ex.Message);
        }
    }

    /// <summary>
    /// List all repos owned by the configured user tagged with a given topic.
    ///
    /// Uses the Forgejo /repos/search?topic=true&amp;q=&lt;topic&gt;&amp;limit=50 endpoint
    /// with proper pagination — byte-faithful port of Python list_repos_with_topic.
    ///
    /// The spec gap noted in the foundation ("use the real Forgejo ?topic=true param")
    /// is CLOSED here: the IForgeClient.SearchReposAsync call did NOT pass topic=true;
    /// we now call the Forgejo HTTP API directly (same pattern as Python) so that
    /// only repos explicitly tagged are returned (not repos whose name matches the topic).
    /// </summary>
    public async Task<List<string>> ListReposWithTopicAsync(string topic, CancellationToken ct)
    {
        // Forgejo /repos/search?topic=true&q=<topic>&limit=50, paginated.
        // IForgeClient.SearchReposAsync maps to /repos/search?q=<query>&limit=<n>
        // (no topic=true flag). We bypass it and call the raw endpoint directly,
        // mirroring the Python implementation byte-for-byte.
        var result = new List<string>();
        int page = 1;
        while (true)
        {
            // Build the URL against the HttpClient base address (already ends with /api/v1/).
            var path = $"repos/search?topic=true&q={Uri.EscapeDataString(topic)}&limit=50&page={page}";
            var raw  = await GetForgeJsonAsync(path, ct);

            // Forgejo wraps results in {"data":[...]} or returns the array directly.
            var items = raw.ValueKind == JsonValueKind.Object &&
                        raw.TryGetProperty("data", out var d) &&
                        d.ValueKind == JsonValueKind.Array
                    ? d
                    : raw.ValueKind == JsonValueKind.Array ? raw
                    : default(JsonElement?);

            if (items is null || !items.Value.EnumerateArray().Any()) break;

            int count = 0;
            foreach (var r in items.Value.EnumerateArray())
            {
                var owner = r.ValueKind == JsonValueKind.Object &&
                            r.TryGetProperty("owner", out var o) &&
                            o.ValueKind == JsonValueKind.Object &&
                            o.TryGetProperty("login", out var l) &&
                            l.ValueKind == JsonValueKind.String
                    ? l.GetString()
                    : null;
                var name = r.ValueKind == JsonValueKind.Object &&
                           r.TryGetProperty("name", out var n) &&
                           n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;
                if (owner == config.ForgejoUser && !string.IsNullOrEmpty(name))
                    result.Add($"{owner}/{name}");
                count++;
            }

            if (count < 50) break; // last page
            page++;
        }
        return result;
    }

    // ── Raw HTTP access to the forge for endpoints not in IForgeClient ─────────

    /// <summary>
    /// Raw GET against the Forgejo API returning parsed JSON.
    /// Used for the topic=true repo search and /commits?since= that IForgeClient doesn't expose.
    ///
    /// Uses the pooled "forge-raw" named client from IHttpClientFactory (item 6 fix:
    /// previously created a new HttpClient per call which is the socket-exhaustion anti-pattern).
    /// </summary>
    private async Task<JsonElement> GetForgeJsonAsync(string path, CancellationToken ct)
    {
        // Use the DI-registered pooled named client — no new HttpClient per call.
        var http2 = httpClientFactory.CreateClient("forge-raw");
        using var resp = await http2.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    /// <summary>List commits for a repo (up to limit, optionally since an ISO date).</summary>
    public async Task<IReadOnlyList<ForgeCommit>> ListCommitsAsync(
        string repoFull, string? since, int limit, CancellationToken ct)
    {
        var (owner, repo) = Split(repoFull);
        try
        {
            // IForgeClient.ListCommitsAsync(owner, repo, sha, limit) — sha param used for filtering.
            // Python uses since= (ISO date). The Gitea adapter doesn't expose since via IForgeClient.
            // Use the direct HTTP route to pass since= parameter.
            return await ListCommitsSinceAsync(owner, repo, since, limit, ct);
        }
        catch (ForgeApiException ex) when (ex.Status == 404)
        {
            return [];
        }
    }

    // Internal: call /repos/{owner}/{repo}/commits?since=&limit= directly.
    // CLOSES the ListCommitsAsync since= gap: passes ISO since to Forgejo as a query param
    // (IForgeClient.ListCommitsAsync only accepts sha filter, not since=).
    private async Task<IReadOnlyList<ForgeCommit>> ListCommitsSinceAsync(
        string owner, string repo, string? since, int limit, CancellationToken ct)
    {
        // Build the Forgejo /commits endpoint with since= if provided.
        // IForgeClient.ListCommitsAsync uses the sha= param which is NOT the same as since=.
        // We call the raw Forgejo API directly to pass since= as a date filter.
        var q = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/commits?limit={limit}";
        if (since is not null)
            q += "&since=" + Uri.EscapeDataString(since);

        try
        {
            var raw = await GetForgeJsonAsync(q, ct);
            if (raw.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<ForgeCommit>();
            foreach (var c in raw.EnumerateArray())
            {
                // Item 10: Python fallback: sha = c.get("sha") or c.get("commit", {}).get("sha")
                // C# must add the nested commit.sha fallback for byte-parity.
                // Forgejo always populates top-level sha, but the fallback ensures
                // correctness for any response shape variation.
                string sha = "";
                if (c.ValueKind == JsonValueKind.Object &&
                    c.TryGetProperty("sha", out var topSha) &&
                    topSha.ValueKind == JsonValueKind.String &&
                    topSha.GetString() is { Length: > 0 } topShaStr)
                {
                    sha = topShaStr;
                }
                else if (c.ValueKind == JsonValueKind.Object &&
                         c.TryGetProperty("commit", out var innerCommit) &&
                         innerCommit.ValueKind == JsonValueKind.Object &&
                         innerCommit.TryGetProperty("sha", out var innerSha) &&
                         innerSha.ValueKind == JsonValueKind.String)
                {
                    sha = innerSha.GetString() ?? "";
                }

                string message = "";
                ForgeCommitAuthor? author = null;
                if (c.ValueKind == JsonValueKind.Object &&
                    c.TryGetProperty("commit", out var inner) &&
                    inner.ValueKind == JsonValueKind.Object)
                {
                    if (inner.TryGetProperty("message", out var msg) &&
                        msg.ValueKind == JsonValueKind.String)
                        message = msg.GetString() ?? "";

                    if (inner.TryGetProperty("author", out var a) &&
                        a.ValueKind == JsonValueKind.Object)
                    {
                        string? name = null, email = null;
                        DateTimeOffset? date = null;
                        string? dateRaw = null;
                        if (a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            name = n.GetString();
                        if (a.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String)
                            email = em.GetString();
                        if (a.TryGetProperty("date", out var dt) && dt.ValueKind == JsonValueKind.String)
                        {
                            dateRaw = dt.GetString();
                            if (DateTimeOffset.TryParse(dateRaw, out var d))
                                date = d;
                        }
                        author = new ForgeCommitAuthor(name, email, date, dateRaw);
                    }
                }

                string? htmlUrl = null;
                if (c.ValueKind == JsonValueKind.Object &&
                    c.TryGetProperty("html_url", out var hu) &&
                    hu.ValueKind == JsonValueKind.String)
                    htmlUrl = hu.GetString();

                result.Add(new ForgeCommit(sha, message, author, htmlUrl));
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning("ListCommitsSince direct HTTP failed for {Owner}/{Repo}: {Message}", owner, repo, ex.Message);
            // Fallback to IForgeClient (no since= filter).
            return await forge.ListCommitsAsync(owner, repo, sha: null, limit: limit, ct: ct);
        }
    }

    // ── Commit pipeline (clone cache) ─────────────────────────────────────────

    public sealed record CommitResult(string RepoFull, string Path, string CommitSha);

    public static readonly string[] KeepDirsWorkspace =
    [
        "conversations", "artifacts", "knowledge/processed", "inbox/from-code", ".bridge"
    ];

    /// <summary>
    /// Ensure keep-dir placeholders exist in the working tree.
    /// Port of Python commit_to_repo keep_dirs parameter.
    /// </summary>
    private static void EnsureKeepDirs(string cloneDir, IEnumerable<string> keepDirs)
    {
        foreach (var d in keepDirs)
        {
            var dPath = Path.Combine(cloneDir, d.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dPath);
            if (!Directory.EnumerateFileSystemEntries(dPath).Any())
                File.WriteAllText(Path.Combine(dPath, ".gitkeep"), "");
        }
    }

    /// <summary>
    /// Clone (or fast-forward) <paramref name="repoFull"/>, write <paramref name="filePath"/>,
    /// commit, push with 3-retry rebase. Returns the resulting commit SHA.
    ///
    /// Mirrors Python commit_to_repo byte-faithfully:
    /// - shallow clone --depth 50
    /// - per-repo lock (SemaphoreSlim)
    /// - git add -A, git commit, git push with rebase retry
    /// - keep_dirs .gitkeep placeholders
    /// - extra_files for atomic multi-file commits
    /// </summary>
    public async Task<CommitResult> CommitToRepoAsync(
        string repoFull,
        string filePath,
        string content,
        string message,
        Dictionary<string, string>? extraFiles = null,
        IEnumerable<string>? keepDirs = null,
        CancellationToken ct = default)
        => await CommitToRepoInternalAsync(
            repoFull, filePath, Encoding.UTF8.GetBytes(content),
            message, extraFiles, keepDirs, ct);

    public async Task<CommitResult> CommitToRepoBinaryAsync(
        string repoFull,
        string filePath,
        byte[] content,
        string message,
        IEnumerable<string>? keepDirs = null,
        CancellationToken ct = default)
        => await CommitToRepoInternalAsync(repoFull, filePath, content, message, null, keepDirs, ct);

    private async Task<CommitResult> CommitToRepoInternalAsync(
        string repoFull,
        string filePath,
        byte[] content,
        string message,
        Dictionary<string, string>? extraFiles,
        IEnumerable<string>? keepDirs,
        CancellationToken ct)
    {
        // Auto-qualify bare repo names BEFORE Split — mirrors Python: repo_full = repo if '/' in repo
        // else f'{user}/{repo}'. Split throws on a bare name, so qualification must come first.
        if (!repoFull.Contains('/'))
            repoFull = $"{config.ForgejoUser}/{repoFull}";

        var (owner, repo) = Split(repoFull);

        // Ensure the target repo exists; auto-create as private under the configured user
        // if it doesn't (Python: if not api_repo_exists(repo_full): api_create_repo(...)).
        if (!await ApiRepoExistsAsync(repoFull, ct))
        {
            if (owner != config.ForgejoUser)
                throw new InvalidOperationException(
                    $"Refusing to auto-create repo under different owner: {repoFull}");
            await ApiCreateRepoAsync(repo, ct);
        }

        var lockSem = await GetRepoLockAsync(repoFull);
        await lockSem.WaitAsync(ct);
        try
        {
            var cloneDir = GetCloneDir(repoFull);
            await EnsureCloneAsync(repoFull, cloneDir, ct);

            // Write primary file.
            var targetPath = Path.Combine(cloneDir, filePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllBytesAsync(targetPath, content, ct);

            // Extra files.
            foreach (var (rel, data) in extraFiles ?? [])
            {
                var ep = Path.Combine(cloneDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(ep)!);
                await File.WriteAllTextAsync(ep, data, Encoding.UTF8, ct);
            }

            // Keep-dir placeholders.
            EnsureKeepDirs(cloneDir, keepDirs ?? []);

            // Stage all.
            await RunGitAsync(["add", "-A"], cloneDir, ct);
            var diff = await RunGitAsync(["diff", "--cached", "--name-only"], cloneDir, ct, check: false);
            if (string.IsNullOrWhiteSpace(diff))
            {
                var head0 = (await RunGitAsync(["rev-parse", "HEAD"], cloneDir, ct) ?? "").Trim();
                return new CommitResult(repoFull, filePath, head0);
            }

            // Commit with committer identity.
            var env = new Dictionary<string, string>
            {
                ["GIT_COMMITTER_NAME"]  = "bridge-mcp",
                ["GIT_COMMITTER_EMAIL"] = $"{config.ForgejoUser}@bridge-mcp.local",
                ["GIT_AUTHOR_NAME"]     = "bridge-mcp",
                ["GIT_AUTHOR_EMAIL"]    = $"{config.ForgejoUser}@bridge-mcp.local",
            };
            await RunGitAsync(["commit", "-m", message], cloneDir, ct, env: env);

            // Push with 3-retry rebase — mirrors Python: rebase + abort only on non-zero exit.
            // FIX: use RunProcessAsync to capture the exit code; RunGitAsync(returnOutput:false)
            // always returned null, making the abort-guard always true (bug: abort on success).
            Exception? lastErr = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await RunGitAsync(["push", "origin", "main"], cloneDir, ct);
                    lastErr = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                    logger.LogWarning("push attempt {N} failed: {Message}", attempt + 1, ex.Message);
                    await RunGitAsync(["fetch", "origin"], cloneDir, ct, check: false);
                    // Capture the rebase exit code — only abort and sleep on actual failure
                    // (Python: if rebase.returncode != 0: abort + sleep).
                    var rebaseProc = await RunProcessAsync(
                        "git", ["rebase", "origin/main"], cloneDir, ct, check: false);
                    if (rebaseProc.ExitCode != 0)
                    {
                        await RunGitAsync(["rebase", "--abort"], cloneDir, ct, check: false);
                        await Task.Delay(500 * (attempt + 1), ct);
                    }
                }
            }
            if (lastErr is not null)
                throw new Exception($"git push failed after retries: {lastErr.Message}", lastErr);

            var head = (await RunGitAsync(["rev-parse", "HEAD"], cloneDir, ct) ?? "").Trim();
            return new CommitResult(repoFull, filePath, head);
        }
        finally
        {
            lockSem.Release();
        }
    }

    // ── Inbox move (mark_inbox_read) ──────────────────────────────────────────

    /// <summary>
    /// Perform a "git mv" in the cached clone: write dest file, delete src file,
    /// git add -A, commit with <paramref name="commitMsg"/>, push with ONE retry
    /// (fetch+rebase on failure — Python _move_file_in_repo pattern).
    ///
    /// The commit message string is observable (asserted in tests):
    ///   "inbox: process {src} -> {dest}"
    /// Must be passed verbatim from the caller.
    ///
    /// Acquires the per-repo lock before touching the working tree.
    /// </summary>
    public async Task<string> MoveInboxFileAsync(
        string repoFull,
        string src,
        string dest,
        string content,
        string commitMsg,
        CancellationToken ct)
    {
        if (!repoFull.Contains('/'))
            repoFull = $"{config.ForgejoUser}/{repoFull}";

        // Ensure repo exists.
        if (!await ApiRepoExistsAsync(repoFull, ct))
            throw new FileNotFoundException($"Repo not found: {repoFull}");

        var lockSem = await GetRepoLockAsync(repoFull);
        await lockSem.WaitAsync(ct);
        try
        {
            var cloneDir = GetCloneDir(repoFull);
            await EnsureCloneAsync(repoFull, cloneDir, ct);

            // Write destination file.
            var destPath = Path.Combine(cloneDir, dest.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await File.WriteAllTextAsync(destPath, content, Encoding.UTF8, ct);

            // Delete source file (if it exists in the working tree).
            var srcPath = Path.Combine(cloneDir, src.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(srcPath))
                File.Delete(srcPath);

            // Stage all changes (write + delete).
            await RunGitAsync(["add", "-A"], cloneDir, ct);
            var diff = await RunGitAsync(["diff", "--cached", "--name-only"], cloneDir, ct, check: false);
            if (string.IsNullOrWhiteSpace(diff))
            {
                // No change — file may have already been processed.
                var head0 = (await RunGitAsync(["rev-parse", "HEAD"], cloneDir, ct) ?? "").Trim();
                return head0;
            }

            // Commit.
            var env = new Dictionary<string, string>
            {
                ["GIT_COMMITTER_NAME"]  = "bridge-mcp",
                ["GIT_COMMITTER_EMAIL"] = $"{config.ForgejoUser}@bridge-mcp.local",
                ["GIT_AUTHOR_NAME"]     = "bridge-mcp",
                ["GIT_AUTHOR_EMAIL"]    = $"{config.ForgejoUser}@bridge-mcp.local",
            };
            await RunGitAsync(["commit", "-m", commitMsg], cloneDir, ct, env: env);

            // Push with ONE retry (Python _move_file_in_repo: one fetch+rebase on failure).
            try
            {
                await RunGitAsync(["push", "origin", "main"], cloneDir, ct);
            }
            catch (Exception)
            {
                await RunGitAsync(["fetch", "origin"], cloneDir, ct, check: false);
                var rebaseProc = await RunProcessAsync(
                    "git", ["rebase", "origin/main"], cloneDir, ct, check: false);
                if (rebaseProc.ExitCode != 0)
                    await RunGitAsync(["rebase", "--abort"], cloneDir, ct, check: false);
                await RunGitAsync(["push", "origin", "main"], cloneDir, ct);
            }

            var head = (await RunGitAsync(["rev-parse", "HEAD"], cloneDir, ct) ?? "").Trim();
            return head;
        }
        finally
        {
            lockSem.Release();
        }
    }

    // ── Git grep (local clone, for search_documents) ──────────────────────────

    /// <summary>
    /// Run git grep -F -n -I --max-count=5 against the cached clone.
    /// Returns list of {repo, path, line, snippet} dicts.
    /// Port of Python git_grep with exact same flags.
    /// </summary>
    public async Task<List<GrepMatch>> GitGrepAsync(
        string repoFull, string query, int maxResults, CancellationToken ct)
    {
        var lockSem = await GetRepoLockAsync(repoFull);
        await lockSem.WaitAsync(ct);
        try
        {
            var cloneDir = GetCloneDir(repoFull);
            await EnsureCloneAsync(repoFull, cloneDir, ct);

            var proc = await RunProcessAsync(
                "git",
                ["grep", "-n", "-I", "--max-count=5", "--no-color", "--fixed-strings", query],
                cloneDir, ct, check: false);

            if (proc.ExitCode != 0 && proc.ExitCode != 1)
                throw new Exception($"git grep failed: {proc.Stderr}");

            var results = new List<GrepMatch>();
            foreach (var rawLine in proc.Stdout.Split('\n'))
            {
                if (string.IsNullOrEmpty(rawLine)) continue;
                var parts = rawLine.Split(':', 3);
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[1], out var lineNum)) continue;
                results.Add(new GrepMatch(repoFull, parts[0], lineNum, PythonStrip(parts[2])));
                if (results.Count >= maxResults) break;
            }
            return results;
        }
        finally
        {
            lockSem.Release();
        }
    }

    // ── Clone management ─────────────────────────────────────────────────────

    private string GetCloneDir(string repoFull)
        => Path.Combine(config.BridgeRepoCache, repoFull.Replace('/', Path.DirectorySeparatorChar));

    private string RemoteUrl(string repoFull)
    {
        var base0 = config.ForgejoBaseUrl.TrimEnd('/');
        var schemeIdx = base0.IndexOf("://", StringComparison.Ordinal);
        var scheme    = schemeIdx >= 0 ? base0[..schemeIdx] : "https";
        var hostPath  = schemeIdx >= 0 ? base0[(schemeIdx + 3)..] : base0;
        var user = Uri.EscapeDataString(config.ForgejoUser);
        var pat  = Uri.EscapeDataString(config.ForgejoPatToken);
        return $"{scheme}://{user}:{pat}@{hostPath}/{repoFull}.git";
    }

    private async Task EnsureCloneAsync(string repoFull, string cloneDir, CancellationToken ct)
    {
        var remote = RemoteUrl(repoFull);
        if (Directory.Exists(Path.Combine(cloneDir, ".git")))
        {
            try
            {
                await RunGitAsync(["remote", "set-url", "origin", remote], cloneDir, ct);
                await RunGitAsync(["fetch", "--prune", "origin"], cloneDir, ct);
                await RunGitAsync(["checkout", "main"], cloneDir, ct, check: false);
                await RunGitAsync(["reset", "--hard", "origin/main"], cloneDir, ct);
                await RunGitAsync(["clean", "-fdx"], cloneDir, ct);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("fast-forward of {Repo} failed ({Message}); recloning", repoFull, ex.Message);
                Directory.Delete(cloneDir, recursive: true);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cloneDir)!);
        await RunGitAsync(["clone", "--depth", "50", remote, cloneDir], null, ct);
        await RunGitAsync(["config", "user.email", $"{config.ForgejoUser}@bridge-mcp.local"], cloneDir, ct);
        await RunGitAsync(["config", "user.name", "bridge-mcp"], cloneDir, ct);
    }

    // ── Repo lock registry ────────────────────────────────────────────────────

    private async Task<SemaphoreSlim> GetRepoLockAsync(string repoFull)
    {
        await _locksGuard.WaitAsync();
        try
        {
            if (!_repoLocks.TryGetValue(repoFull, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _repoLocks[repoFull] = sem;
            }
            return sem;
        }
        finally
        {
            _locksGuard.Release();
        }
    }

    // ── Git process helpers ───────────────────────────────────────────────────

    private static async Task<string?> RunGitAsync(
        string[] args,
        string? cwd,
        CancellationToken ct,
        bool check = true,
        bool returnOutput = true,
        Dictionary<string, string>? env = null)
    {
        var proc = await RunProcessAsync("git", args, cwd, ct, check, env);
        return returnOutput ? proc.Stdout : null;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<ProcessResult> RunProcessAsync(
        string exe,
        string[] args,
        string? cwd,
        CancellationToken ct,
        bool check = true,
        Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = cwd ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new Exception($"Failed to start {exe}");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        if (check && p.ExitCode != 0)
            throw new Exception(
                $"git command failed ({p.ExitCode}): {string.Join(" ", args)}\n"
                + $"stdout: {stdout}\nstderr: {stderr}");

        return new ProcessResult(p.ExitCode, stdout, stderr);
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Strip leading and trailing whitespace matching Python's str.strip() semantics.
    ///
    /// Python str.strip() removes ALL Unicode whitespace including the four C0
    /// information-separator control characters U+001C (FS), U+001D (GS), U+001E (RS),
    /// U+001F (US), which char.IsWhiteSpace does NOT treat as whitespace in .NET.
    ///
    /// Item 8: grep snippet trimming parity — use this instead of .Trim() at grep
    /// line boundaries so that lines ending in U+001C–U+001F are stripped identically
    /// to Python's snippet.strip().
    /// </summary>
    internal static string PythonStrip(string s)
    {
        // Fast path: no chars to strip.
        if (s.Length == 0) return s;

        static bool IsPythonWhitespace(char c)
            => char.IsWhiteSpace(c) || c is '\x1C' or '\x1D' or '\x1E' or '\x1F';

        int start = 0;
        while (start < s.Length && IsPythonWhitespace(s[start]))
            start++;

        int end = s.Length - 1;
        while (end > start && IsPythonWhitespace(s[end]))
            end--;

        return start > end ? "" : s[start..(end + 1)];
    }

    private static (string owner, string repo) Split(string repoFull)
    {
        var idx = repoFull.IndexOf('/');
        if (idx < 0) throw new ArgumentException($"Expected owner/repo, got: {repoFull}");
        return (repoFull[..idx], repoFull[(idx + 1)..]);
    }
}

/// <summary>A single git grep match.</summary>
public sealed record GrepMatch(string Repo, string Path, int Line, string Snippet);
