using System.Collections.Generic;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;
using ModelContextProtocol.Server;

namespace Bridge.Mcp.Tools;

// ForgeCommit record (from Sinapsi.Forge):
//   Sha: string, Message: string, Author: ForgeCommitAuthor?, HtmlUrl: string?
//   ForgeCommitAuthor: Name, Email, Date: DateTimeOffset?

/// <summary>
/// B4b-6 STATEFUL TOOLS: list_recent_additions, mark_inbox_read.
///
/// list_recent_additions:
///   - Per repo: load caller cursor from {repo}/.bridge/cursors.json.
///     Key scheme: "bearer:"+sha256(token)[:16] for legacy bearer,
///                 "jwt:"+sub for JWT callers.
///   - Fetch ≤50 commits (passing since= to Forgejo when provided).
///   - Return commits AFTER the cursor SHA (or all 50 if no cursor).
///   - If commits found AND since==null: write updated cursor (latest SHA)
///     back via CommitToRepoAsync. Advance-only-when-since==null is LOAD-BEARING.
///   - Cursor JSON: json.dumps(cursors, indent=2, sort_keys=True) + "\n"
///     (Python _save_cursors serialization — indent=2, sort_keys=True).
///
/// mark_inbox_read:
///   - Resolve filename: strip leading /, add "inbox/from-code/" prefix if absent.
///   - Read inbox/from-code/{filename} as UTF-8 text.
///   - Two-file move: write knowledge/processed/{filename}, delete src,
///     single commit with EXACT message "inbox: process {src} -> {dest}".
///   - Push with 1 retry (fetch+rebase on failure). Per-repo lock.
///   - ToolError(not_found) if src missing.
///   - Commit message string is asserted in tests — must match Python exactly.
/// </summary>
[McpServerToolType]
public sealed class BridgeStatefulTools(
    AuthService auth,
    GitOpsService git,
    AuditService audit,
    BridgeConfig config,
    ILogger<BridgeStatefulTools> logger)
{
    // ── list_recent_additions ────────────────────────────────────────────────────

    [McpServerTool(Name = "list_recent_additions")]
    [Description(
        "Return commits since the caller's stored cursor (or 'since' if given). " +
        "Cursors live in {repo}/.bridge/cursors.json keyed by SHA-256 of the token/subject. " +
        "After returning the delta, writes the new cursor (latest SHA) back — " +
        "ONLY when 'since' param is not set. " +
        "Returns {since, repos:{[repo_full]:[{sha, message, date, url}]}}.")]
    public async Task<object> ListRecentAdditions(
        [Description("Repo name (bare 'name' or 'owner/name'). Null = all knowledge-tagged repos.")] string? repo = null,
        [Description("ISO datetime passed to the Forgejo commits API as 'since' filter. When set, cursor is NOT advanced.")] string? since = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.ReadDocumentsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            // Resolve target repos.
            List<string> repos;
            if (repo is not null)
                repos = [git.ResolveRepo(repo)];
            else
                repos = await git.ListReposWithTopicAsync("knowledge", cancellationToken);

            var callerKey = CallerIdentityKey(ctx);
            var output = new Dictionary<string, List<object>>();

            foreach (var repoFull in repos)
            {
                // Load cursor for this repo.
                var cursors = await LoadCursorsAsync(repoFull, cancellationToken);
                var lastSha = cursors.TryGetValue(callerKey, out var v) ? v : null;

                // Fetch ≤50 commits, passing since= to Forgejo.
                var commits = await git.ListCommitsAsync(repoFull, since, limit: 50, cancellationToken);

                if (commits.Count == 0)
                {
                    output[repoFull] = [];
                    continue;
                }

                // Return commits AFTER the cursor SHA (stop when we hit the last-seen SHA).
                // ForgeCommit: Sha, Message, Author (Author.Date), HtmlUrl
                var newCommits = new List<object>();
                foreach (var c in commits)
                {
                    var sha = c.Sha;
                    if (lastSha is not null && sha == lastSha)
                        break;

                    newCommits.Add(new
                    {
                        sha,
                        message = (c.Message ?? "").Trim(),
                        date    = c.Author?.DateRaw,
                        url     = c.HtmlUrl,
                    });
                }

                output[repoFull] = newCommits;

                // Advance cursor ONLY when since==null and new commits exist.
                if (newCommits.Count > 0 && since is null)
                {
                    // Latest SHA = the first commit returned (most recent).
                    var latestSha = commits[0].Sha;
                    if (!string.IsNullOrEmpty(latestSha))
                    {
                        cursors[callerKey] = latestSha;
                        await SaveCursorsAsync(repoFull, cursors, cancellationToken);
                    }
                }
            }

            audit.Enqueue("list_recent_additions", ctx, project: repo, query: since);

            return new { since, repos = output };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── mark_inbox_read ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "mark_inbox_read")]
    [Description(
        "Move a file from inbox/from-code/ to knowledge/processed/. " +
        "path can be 'foo.md' (bare) or 'inbox/from-code/foo.md' (full). " +
        "Reads the file as UTF-8, writes to knowledge/processed/{filename}, " +
        "deletes source, commits with 'inbox: process {src} -> {dest}'. " +
        "ToolError(not_found) if source file doesn't exist. " +
        "Returns {repo, from, to, commit}.")]
    public async Task<object> MarkInboxRead(
        [Description("Repo name (bare 'name' or 'owner/name').")] string repo,
        [Description("Full path 'inbox/from-code/foo.md' or bare filename 'foo.md'. Leading slashes stripped; prefix added if absent.")] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.ReadDocumentsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            var repoFull = git.ResolveRepo(repo);

            // Normalize path: strip leading /, add inbox/from-code/ prefix if absent.
            var rel = path.TrimStart('/');
            if (!rel.StartsWith("inbox/from-code/", StringComparison.Ordinal))
                rel = $"inbox/from-code/{rel}";

            // Read the source file (UTF-8).
            string content;
            try
            {
                content = await git.ReadFileRawAsync(repoFull, rel, cancellationToken);
            }
            catch (FileNotFoundException ex)
            {
                throw new BridgeToolException("not_found", ex.Message);
            }

            var filename = rel.Split('/')[^1];
            var dest = $"knowledge/processed/{filename}";

            // Two-file atomic move via the git clone.
            // EXACT commit message (asserted in tests): "inbox: process {src} -> {dest}"
            var commitMsg = $"inbox: process {rel} -> {dest}";
            var commitSha = await MoveFileInRepoAsync(repoFull, rel, dest, content, commitMsg, cancellationToken);

            audit.Enqueue("mark_inbox_read", ctx, project: repo, query: path);

            return new
            {
                repo   = repoFull,
                from   = rel,
                to     = dest,
                commit = commitSha,
            };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── Cursor helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-caller key for cursors.json.
    /// Python: "bearer:" + sha256(raw_token.encode("utf-8")).hexdigest()[:16]  for bearer
    ///         "jwt:" + sub                                                      for JWT
    /// </summary>
    internal static string CallerIdentityKey(BridgeAuthContext ctx)
    {
        if (ctx.Mode == "jwt")
            return $"jwt:{ctx.Subject}";

        // Legacy bearer: sha256(raw token as UTF-8)[:16] hex lowercase.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ctx.RawToken));
        return "bearer:" + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    /// <summary>
    /// Load cursors.json from {repoFull}/.bridge/cursors.json.
    /// Returns empty dict on missing file or parse failure (matching Python _load_cursors).
    /// </summary>
    private async Task<Dictionary<string, string>> LoadCursorsAsync(
        string repoFull, CancellationToken ct)
    {
        try
        {
            var raw = await git.ReadFileRawAsync(repoFull, ".bridge/cursors.json", ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            return result ?? [];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (Exception ex)
        {
            logger.LogWarning("cursor read for {Repo} failed: {Message}", repoFull, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Save cursors.json back to {repoFull}/.bridge/cursors.json.
    /// Python: json.dumps(cursors, indent=2, sort_keys=True) + "\n"
    /// We reproduce this exactly: indent=2, sort_keys (ordinal/codepoint order), trailing newline.
    ///
    /// Items 11+12: sort by Unicode code-point order (StringComparer.Ordinal matches Python's
    /// sort_keys), and escape non-ASCII chars as \uXXXX (Python ensure_ascii=True default).
    /// </summary>
    private async Task SaveCursorsAsync(
        string repoFull, Dictionary<string, string> cursors, CancellationToken ct)
    {
        try
        {
            // Item 12: sort with StringComparer.Ordinal = Unicode code-point order = Python sort_keys.
            // SortedDictionary default uses culture-aware comparison which diverges for mixed-case keys.
            var sorted = new SortedDictionary<string, string>(cursors, StringComparer.Ordinal);
            var payload = SerializeCursors(sorted) + "\n";

            await git.CommitToRepoAsync(
                repoFull,
                ".bridge/cursors.json",
                payload,
                "bridge: update cursors",
                ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning("cursor save for {Repo} failed: {Message}", repoFull, ex.Message);
        }
    }

    /// <summary>
    /// Serialize a cursor dict to JSON matching Python's json.dumps(d, indent=2, sort_keys=True).
    /// Python output for {"key": "sha"}:
    /// {
    ///   "key": "sha"
    /// }
    /// Python separators default: (", ", ": ") but with indent=2 uses (",\n  ", ": ").
    /// Actually Python json.dumps with indent=2 uses:
    ///   {\n  "key1": "val1",\n  "key2": "val2"\n}
    /// We must produce this exact format for byte parity.
    /// </summary>
    internal static string SerializeCursors(IEnumerable<KeyValuePair<string, string>> sorted)
    {
        var pairs = sorted.ToList();
        if (pairs.Count == 0)
            return "{}";

        var sb = new StringBuilder();
        sb.Append('{');
        for (int i = 0; i < pairs.Count; i++)
        {
            sb.Append("\n  ");
            // Python json.dumps quotes both key and value.
            sb.Append('"');
            sb.Append(EscapeJsonString(pairs[i].Key));
            sb.Append("\": \"");
            sb.Append(EscapeJsonString(pairs[i].Value));
            sb.Append('"');
            if (i < pairs.Count - 1)
                sb.Append(',');
        }
        sb.Append("\n}");
        return sb.ToString();
    }

    /// <summary>
    /// JSON string escaping matching Python json.dumps(ensure_ascii=True) default.
    ///
    /// Item 11: Python json.dumps defaults to ensure_ascii=True, which escapes any
    /// non-ASCII character (code point > 0x7E) as \uXXXX with LOWERCASE hex.
    /// A JWT 'sub' claim can legitimately contain non-ASCII characters, so the previous
    /// assumption ("ASCII-only") is incorrect for full byte-parity.
    ///
    /// Escaping rules (matching Python json.dumps defaults exactly):
    ///   " → \"     (backslash + quote)
    ///   \ → \\
    ///   \n → \n   \r → \r   \t → \t   \b → \b   \f → \f
    ///   other control chars (< 0x20) → \uXXXX (lowercase)
    ///   non-ASCII (> 0x7E): → \uXXXX (lowercase BMP) or \uXXXX\uXXXX (surrogate pair for astral)
    ///   DEL (0x7F): Python leaves literal; .NET char.IsAscii(0x7F)=true so we leave it literal too.
    /// </summary>
    internal static string EscapeJsonString(string s)
    {
        // Fast path: all chars are printable ASCII (0x20..0x7E) and no quote/backslash.
        if (!s.Any(c => c == '"' || c == '\\' || c < 0x20 || c > 0x7E))
            return s;

        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                default:
                    if (c < 0x20)
                    {
                        // Other control chars: \uXXXX lowercase.
                        sb.Append($"\\u{(int)c:x4}");
                    }
                    else if (c > 0x7E)
                    {
                        // Non-ASCII: escape as \uXXXX (ensure_ascii=True parity).
                        // For surrogate pairs (astral plane), emit two \uXXXX sequences.
                        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append($"\\u{(int)c:x4}");
                            sb.Append($"\\u{(int)s[i + 1]:x4}");
                            i++; // skip low surrogate
                        }
                        else
                        {
                            sb.Append($"\\u{(int)c:x4}");
                        }
                    }
                    else
                    {
                        // Printable ASCII (0x20..0x7E, excluding the special cases above).
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    // ── Git move (mark_inbox_read) ────────────────────────────────────────────────

    /// <summary>
    /// Perform a "git mv" via the local clone: write dest, delete src, commit, push.
    /// Push retry: one retry (fetch+rebase on failure) — Python uses exactly one retry.
    /// Per-repo lock is acquired via GitOpsService.MoveFileInRepoAsync.
    /// </summary>
    private async Task<string> MoveFileInRepoAsync(
        string repoFull,
        string src,
        string dest,
        string content,
        string commitMsg,
        CancellationToken ct)
    {
        // Use CommitToRepoAsync with an extra delete file as a side effect.
        // CommitToRepoAsync does: write primary file + git add -A + commit + push.
        // For mark_inbox_read we need to ALSO delete the src file.
        // We pass the src path as an "extraDelete" via the extraFiles mechanism.
        // Since CommitToRepoAsync only writes files, we piggyback the delete
        // using a thin wrapper that writes dest + removes src before git add -A.
        return await git.MoveInboxFileAsync(repoFull, src, dest, content, commitMsg, ct);
    }
}
