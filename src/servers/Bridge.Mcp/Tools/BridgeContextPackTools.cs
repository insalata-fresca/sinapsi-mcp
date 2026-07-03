using System.ComponentModel;
using System.Text.Json;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;
using ModelContextProtocol.Server;

namespace Bridge.Mcp.Tools;

/// <summary>
/// B4b-7 get_context_pack: 3-priority-tier greedy assembler.
///
/// Budget: CONTEXT_PACK_CHAR_BUDGET (default 30000 chars).
/// Section size = len(json.dumps(section)) — JSON serialization length,
/// using ensure_ascii=False equivalent (UnsafeRelaxedJsonEscaping).
/// STOP (never truncate mid-section) when budget exceeded.
///
/// Tier 1: personal-facts grep, max 10 results.
/// Tier 2: all knowledge repos + personal-archive grep, max 5 each, one section per repo.
/// Tier 3 (if scope in all|server): read hard-coded state files from
///   {FORGEJO_USER}/home-server: state/lxc-inventory.md, state/zfs.md,
///   state/npm-hosts.md, state/services.md.
///
/// Per-leg failures are non-fatal (logged + skipped).
/// Reuses GitOpsService.GitGrepAsync + ListReposWithTopicAsync directly
/// (no BridgeGrepTools dependency — tools are standalone in DI).
/// </summary>
[McpServerToolType]
public sealed class BridgeContextPackTools(
    AuthService auth,
    GitOpsService git,
    AuditService audit,
    BridgeConfig config,
    ILogger<BridgeContextPackTools> logger)
{
    // Hard-coded state file list (matches Python _state_files_to_include exactly).
    private static readonly string[] StateFiles =
    [
        "state/lxc-inventory.md",
        "state/zfs.md",
        "state/npm-hosts.md",
        "state/services.md",
    ];

    // JsonSerializerOptions with ensure_ascii=False equivalent (Python default).
    // Python: json.dumps(section, ensure_ascii=False) — non-ASCII chars not escaped.
    // C# default escapes non-ASCII chars; UnsafeRelaxedJsonEscaping matches Python.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "get_context_pack")]
    [Description(
        "Compose a context pack for 'topic' capped at CONTEXT_PACK_CHAR_BUDGET (30000 chars). " +
        "3 priority tiers: (1) personal-facts grep max10, (2) knowledge+archive repos grep max5 each, " +
        "(3) if scope in [all,server]: reads state/*.md files from {user}/home-server. " +
        "Section size = JSON length. STOP (never truncate) when budget exceeded. " +
        "Returns {topic, scope, budget, used, sections}.")]
    public async Task<object> GetContextPack(
        [Description("Topic string used as the grep query for facts + documents fan-out.")] string topic,
        [Description("Scope: 'all' (default) | 'server' | other. 'all'/'server' includes home-server state files.")] string scope = "all",
        CancellationToken cancellationToken = default)
    {
        try { return await GetContextPackCore(topic, scope, cancellationToken); }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    private async Task<object> GetContextPackCore(
        string topic,
        string scope,
        CancellationToken cancellationToken)
    {
        var ctx = auth.Authorize(
            AuthService.ContextPackScope,
            AuthService.ReadBucket,
            config.RateLimitReadPerMin);

        var budget   = config.ContextPackCharBudget;
        var used     = 0;
        var sections = new List<object>();

        // Local helper: attempt to append a section; returns false if it doesn't fit.
        bool TryAdd(object section)
        {
            var size = JsonLength(section);
            if (used + size > budget) return false;
            sections.Add(section);
            used += size;
            return true;
        }

        // ── Tier 1: personal facts ────────────────────────────────────────────
        try
        {
            var factsRepo = git.FactsRepo();
            if (await git.ApiRepoExistsAsync(factsRepo, cancellationToken))
            {
                var factsMatches = await git.GitGrepAsync(factsRepo, topic, maxResults: 10, cancellationToken);
                if (factsMatches.Count > 0)
                {
                    TryAdd(new
                    {
                        section  = "facts",
                        matches  = factsMatches.Select(ToMatchDto).ToList(),
                    });
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("context_pack facts grep failed: {Message}", ex.Message);
        }

        // ── Tier 2: knowledge repos + archive ─────────────────────────────────
        try
        {
            // Build target list: all knowledge-tagged repos + personal-archive.
            // Dedup preserving order (mirrors Python seen-set logic).
            var docTargets = new List<string>();
            docTargets.AddRange(await git.ListReposWithTopicAsync("knowledge", cancellationToken));
            docTargets.Add(git.ArchiveRepo());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var deduped = docTargets.Where(t => seen.Add(t)).ToList();

            foreach (var repo in deduped)
            {
                if (used >= budget) break;
                try
                {
                    var repoMatches = await git.GitGrepAsync(repo, topic, maxResults: 5, cancellationToken);
                    if (repoMatches.Count == 0) continue;

                    if (!TryAdd(new
                    {
                        section  = "documents",
                        repo,
                        matches  = repoMatches.Select(ToMatchDto).ToList(),
                    })) break; // budget exceeded — stop Tier 2
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("context_pack repo {Repo} grep failed: {Message}", repo, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("context_pack documents fan-out failed: {Message}", ex.Message);
        }

        // ── Tier 3: server state files ────────────────────────────────────────
        // Python: `if scope.lower() in ("all", "server"):` — NO strip/Trim (parity item 4).
        // Remove .Trim() so whitespace-padded scope (" all") doesn't include Tier-3 sections.
        if (scope.ToLowerInvariant() is "all" or "server")
        {
            var homeServer = $"{config.ForgejoUser}/home-server";
            foreach (var statePath in StateFiles)
            {
                if (used >= budget) break;
                try
                {
                    var content = await git.ReadFileRawAsync(homeServer, statePath, cancellationToken);
                    if (!TryAdd(new
                    {
                        section  = "server-state",
                        path     = statePath,
                        content,
                    })) break;
                }
                catch (FileNotFoundException)
                {
                    // File doesn't exist in the repo — skip silently (non-fatal).
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("context_pack state file {Path} read failed: {Message}", statePath, ex.Message);
                }
            }
        }

        audit.Enqueue("get_context_pack", ctx, project: null, query: topic);

        return new
        {
            topic,
            scope,
            budget,
            used,
            sections,
        };
    }

    // ── JSON length measurement ───────────────────────────────────────────────

    /// <summary>
    /// Measure a section's size as len(json.dumps(section, ensure_ascii=False)).
    ///
    /// Python's DEFAULT json.dumps uses separators (", ", ": ") — one space after
    /// every "," and ":" at the structural level. C# System.Text.Json emits compact
    /// JSON with NO spaces, so the budget diverges near the boundary.
    ///
    /// Fix: serialize compact (UnsafeRelaxedJsonEscaping for ensure_ascii=False parity),
    /// then add 1 for each STRUCTURAL ":" and "," (i.e. those not inside a JSON string).
    /// Each structural separator gets one extra space in Python's default format.
    ///
    /// Proof: compact_len + count_structural(":") + count_structural(",")
    ///        == len(json.dumps(section, ensure_ascii=False))   ∀ section
    /// </summary>
    internal static int JsonLength(object section)
    {
        var compact = JsonSerializer.Serialize(section, JsonOpts);
        return compact.Length + CountStructuralSeparators(compact);
    }

    /// <summary>
    /// Count structural ":" and "," characters in a compact JSON string —
    /// those that are NOT inside a JSON string value.
    /// Each counts as +1 because Python's default json.dumps inserts one space after each.
    /// </summary>
    internal static int CountStructuralSeparators(string compactJson)
    {
        var count = 0;
        var inString = false;
        var escapeNext = false;

        foreach (var c in compactJson)
        {
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escapeNext = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (!inString && (c == ':' || c == ','))
                count++;
        }

        return count;
    }

    private static object ToMatchDto(GrepMatch m)
        => new { repo = m.Repo, path = m.Path, line = m.Line, snippet = m.Snippet };
}
