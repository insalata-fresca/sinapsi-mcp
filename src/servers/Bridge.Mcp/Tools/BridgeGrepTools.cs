using System.ComponentModel;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;
using ModelContextProtocol.Server;

namespace Bridge.Mcp.Tools;

/// <summary>
/// B4b-4 GREP TOOLS: search_documents and lookup_fact.
///
/// Both reuse GitOpsService.GitGrepAsync (git grep -F -n -I --max-count=5).
/// Per-repo cap: 20. Overall cap: 100. Repo iteration order preserved.
/// Per-repo failures are logged, not surfaced (matches Python behavior).
///
/// lookup_fact sensitive path:
///   - Phase-5 STUB: REGARDLESS of auth outcome, always returns {status:scope_required}.
///   - Auth denial still records audit with outcome="denied".
///   - Sensitive query is sha256:hex16-redacted in audit log (handled by AuditService.Enqueue).
/// </summary>
[McpServerToolType]
public sealed class BridgeGrepTools(
    AuthService auth,
    GitOpsService git,
    AuditService audit,
    BridgeConfig config,
    ILogger<BridgeGrepTools> logger)
{
    private const int PerRepoCap = 20;
    private const int OverallCap = 100;

    // ── search_documents ──────────────────────────────────────────────────────

    [McpServerTool(Name = "search_documents")]
    [Description(
        "Grep 'query' across knowledge repos and/or personal-archive. " +
        "scope: 'all' (default) | 'projects' | 'archive'. " +
        "'since' is accepted but not used by git-grep. " +
        "Returns {query, scope, matches:[{repo, path, line, snippet}]}. " +
        "Per-repo failures are silently skipped. Overall cap: 100 matches.")]
    public async Task<object> SearchDocuments(
        [Description("Fixed-string grep pattern. Must not be empty.")] string query,
        [Description("Scope: 'all' | 'projects' | 'archive'. Default: 'all'.")] string scope = "all",
        [Description("ISO datetime (accepted but not used by git-grep).")] string? since = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.ReadDocumentsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            if (string.IsNullOrWhiteSpace(query))
                throw new BridgeToolException("invalid_query", "query must not be empty");

            // Python: scope_l = scope.lower() — NO strip/Trim (parity item 3).
            // Whitespace-padded scope ("all ") must NOT be trimmed: it would give 0 matches
            // in Python but full results after C# Trim. Remove .Trim() to match Python.
            var scopeL  = scope.ToLowerInvariant();
            var targets = await ResolveTargetsAsync(scopeL, cancellationToken);
            var matches = await FanOutGrepAsync(targets, query, PerRepoCap, OverallCap, cancellationToken);

            audit.Enqueue("search_documents", ctx, project: null, query: query);

            return new { query, scope = scopeL, matches };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── lookup_fact ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "lookup_fact")]
    [Description(
        "Grep 'query' against the personal-facts repo. " +
        "sensitive=false (default): requires bridge:read:facts scope; returns {query, sensitive:false, matches}. " +
        "sensitive=true: Phase-5 stub — ALWAYS returns {status:'scope_required'} regardless of auth. " +
        "Sensitive-path auth denial is still audited with outcome='denied'.")]
    public async Task<object> LookupFact(
        [Description("Fixed-string grep pattern against personal-facts repo.")] string query,
        [Description("If true, requires bridge:read:facts_sensitive scope (Phase-5 stub; always returns scope_required).")] bool sensitive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (sensitive)
                return HandleSensitiveLookup(query);

            // Non-sensitive path.
            var ctx = auth.Authorize(
                AuthService.ReadFactsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            var factsRepo = git.FactsRepo();
            var matches   = new List<GrepMatch>();

            try
            {
                if (await git.ApiRepoExistsAsync(factsRepo, cancellationToken))
                    matches = await git.GitGrepAsync(factsRepo, query, maxResults: 20, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("personal-facts grep failed: {Message}", ex.Message);
            }

            audit.Enqueue("lookup_fact", ctx, project: null, query: query, sensitive: false);

            return new
            {
                query,
                sensitive = false,
                matches   = matches.Select(m => new { repo = m.Repo, path = m.Path, line = m.Line, snippet = m.Snippet }).ToList(),
            };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Phase-5 stub: regardless of auth outcome, return scope_required.
    /// If auth DENIES (insufficient_scope / missing_bearer), audit with outcome="denied".
    /// If rate_limited: re-throw so the caller sees a real rate_limited error (Python parity).
    ///
    /// Python lookup_fact sensitive path catches ONLY AuthError (insufficient_scope/missing_bearer).
    /// ToolError("rate_limited") is NOT an AuthError, so it propagates unhandled → real tool error.
    /// C# must replicate this: catch only the scope-denial exceptions, NOT rate_limited.
    /// </summary>
    private object HandleSensitiveLookup(string query)
    {
        // Attempt to authorize (for the rate-limit + audit-denied path).
        // ORDERING: authorize FIRST (can raise rate_limited or insufficient_scope),
        // THEN return the Phase-5 stub.
        BridgeAuthContext? ctx = null;
        string outcome         = "allowed-phase5-stub";
        try
        {
            ctx = auth.Authorize(
                AuthService.ReadFactsSensitiveScope,
                AuthService.SensitiveBucket,
                config.RateLimitSensitivePerMin);
        }
        catch (BridgeToolException ex) when (ex.Code == "rate_limited")
        {
            // Rate-limit exceeded: re-throw so it surfaces as a real tool error.
            // Python: ToolError("rate_limited") is NOT caught by `except AuthError:`,
            // so it propagates to FastMCP and surfaces as a tool error.
            // The audit for rate_limited is NOT written (Python does not write it either).
            throw;
        }
        catch (BridgeToolException ex) when (ex.Code is "insufficient_scope" or "missing_bearer")
        {
            // Auth was denied (scope/bearer) — still return scope_required (Phase-5 stub),
            // but audit with the REAL caller identity (Python: ctx=_current_auth.get()).
            ctx     = BridgeAuthState.CurrentAuth; // real caller, even if unauthorized
            outcome = "denied";
        }

        audit.Enqueue(
            "lookup_fact",
            ctx,
            project: null,
            query: query,
            sensitive: true,
            outcome: outcome);

        // Phase-5 Vaultwarden stub: ALWAYS return scope_required.
        // Two slightly different notes (matching Python exactly):
        //   - auth-denied: "Sensitive facts require Phase 5 OAuth scope."
        //   - auth-allowed: "Phase 5 Vaultwarden integration not yet wired."
        return outcome == "denied"
            ? (object)new
            {
                status = "scope_required",
                scope  = AuthService.ReadFactsSensitiveScope,
                note   = "Sensitive facts require Phase 5 OAuth scope.",
            }
            : new
            {
                status = "scope_required",
                scope  = AuthService.ReadFactsSensitiveScope,
                note   = "Phase 5 Vaultwarden integration not yet wired.",
            };
    }

    /// <summary>
    /// Build the repo target list for search_documents based on scope.
    /// Deduplicates the list (matches Python seen-set logic).
    /// Exposed as internal for testing.
    /// </summary>
    internal async Task<List<string>> ResolveTargetsAsync(string scopeL, CancellationToken ct)
    {
        var targets = new List<string>();
        if (scopeL is "all" or "projects")
            targets.AddRange(await git.ListReposWithTopicAsync("knowledge", ct));
        if (scopeL is "all" or "archive")
            targets.Add(git.ArchiveRepo());

        // Dedup preserving order (mirrors Python: [t for t in targets if not (t in seen or seen.add(t))]).
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<string>();
        foreach (var t in targets)
        {
            if (seen.Add(t)) deduped.Add(t);
        }
        return deduped;
    }

    /// <summary>
    /// Fan out grep over repos. Per-repo cap = maxPerRepo; overall cap = overallCap.
    /// Per-repo failures are logged and skipped (not surfaced).
    /// Returns matches in repo-iteration order. Exposed as internal for testing.
    /// </summary>
    internal async Task<List<object>> FanOutGrepAsync(
        IEnumerable<string> repos,
        string query,
        int maxPerRepo,
        int overallCap,
        CancellationToken ct)
    {
        var matches = new List<object>();
        foreach (var repo in repos)
        {
            if (matches.Count >= overallCap) break;
            try
            {
                var repoMatches = await git.GitGrepAsync(repo, query, maxResults: maxPerRepo, ct);
                foreach (var m in repoMatches)
                {
                    if (matches.Count >= overallCap) break;
                    matches.Add(new { repo = m.Repo, path = m.Path, line = m.Line, snippet = m.Snippet });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("git grep on {Repo} failed: {Message}", repo, ex.Message);
            }
        }
        return matches;
    }
}
