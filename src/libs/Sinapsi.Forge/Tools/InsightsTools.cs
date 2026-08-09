using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>
/// Repository <b>Insights</b> tools — the read-only analytics surface a human gets from a
/// repo's "Insights" tab: traffic, contributor/commit statistics, the community profile,
/// the dependency-graph SBOM, forks, and language breakdown.
///
/// <para>
/// <b>GitHub-only.</b> Gitea/Forgejo/Codeberg expose no analogue, so
/// <c>GiteaForgeClient</c> throws <see cref="ForgeNotSupportedException"/> for every method
/// here and this class is registered by the <c>Github.Mcp</c> host ONLY — the mirror of how
/// <see cref="TimeTrackingTools"/> is Gitea-only. The gate is
/// <see cref="ForgeCapabilities.Insights"/>.
/// </para>
///
/// <para>
/// <b>Three upstream answers are NOT failures</b> and are surfaced as structured envelopes
/// rather than exceptions, because reading them as tool errors would be a lie:
/// </para>
/// <list type="bullet">
///   <item><c>202</c> on any <c>stats/*</c> endpoint — GitHub computes those caches
///     asynchronously and answers <c>202 Accepted</c> with an EMPTY body on the first ask.
///     The tool returns <c>{ ok:false, status:202, retry:true, note }</c>; the CALLER retries
///     a few seconds later. Nothing polls or blocks inside the tool.</item>
///   <item><c>403</c> on any <c>traffic/*</c> endpoint — GitHub requires push access to read
///     traffic. Expected on a repo we can read but not write; returns
///     <c>{ ok:false, status:403, note }</c>.</item>
///   <item><c>422</c> on <c>get_code_frequency</c> — the repo has too many commits for GitHub
///     to compute the series; returns <c>{ ok:false, status:422, note }</c>.</item>
/// </list>
///
/// <para>
/// A <c>404</c> (and every other non-2xx) stays on the normal path: <c>ForgeApiException</c>
/// → <see cref="ForgeToolGuard"/> → <c>{ ok:false, status:404, error }</c> with the upstream
/// body scrubbed. Path-segment params are validated at the top of each tool via
/// <see cref="SinapsiForgeValidation"/>, before any HTTP call.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class InsightsTools
{
    /// <summary>Traffic granularity accepted by GitHub's views/clones endpoints.</summary>
    internal static readonly string[] PerValues = ["day", "week"];

    /// <summary>Sort orders accepted by GitHub's forks endpoint.</summary>
    internal static readonly string[] ForkSortValues = ["newest", "oldest", "stargazers", "watchers"];

    // ── Traffic (requires push access; 403 is an answer, not a failure) ────────

    [McpServerTool(Name = "get_traffic_views", ReadOnly = true)]
    [Description("Page views for a repository over the last 14 days, with totals and a per-day (or per-week) series. Requires push access; returns a 403 envelope otherwise.")]
    public static Task<object> GetTrafficViews(IForgeClient forge, string owner, string repo,
        [Description("Granularity: \"day\" (default) or \"week\".")] string per = "day",
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateChoice(per, "per", PerValues),
            async () => await forge.GetTrafficViewsAsync(owner, repo, per, ct));

    [McpServerTool(Name = "get_traffic_clones", ReadOnly = true)]
    [Description("Git clones of a repository over the last 14 days, with totals and a per-day (or per-week) series. Requires push access; returns a 403 envelope otherwise.")]
    public static Task<object> GetTrafficClones(IForgeClient forge, string owner, string repo,
        [Description("Granularity: \"day\" (default) or \"week\".")] string per = "day",
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateChoice(per, "per", PerValues),
            async () => await forge.GetTrafficClonesAsync(owner, repo, per, ct));

    [McpServerTool(Name = "get_traffic_referrers", ReadOnly = true)]
    [Description("Top 10 referring sites for a repository over the last 14 days. Requires push access; returns a 403 envelope otherwise.")]
    public static Task<object> GetTrafficReferrers(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetTrafficReferrersAsync(owner, repo, ct));

    [McpServerTool(Name = "get_traffic_paths", ReadOnly = true)]
    [Description("Top 10 most-visited paths in a repository over the last 14 days. Requires push access; returns a 403 envelope otherwise.")]
    public static Task<object> GetTrafficPaths(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetTrafficPathsAsync(owner, repo, ct));

    // ── Activity / contributors (stats/* answer 202 while GitHub warms the cache) ─

    [McpServerTool(Name = "list_contributors", ReadOnly = true)]
    [Description("List repository contributors ordered by commit count.")]
    public static Task<object> ListContributors(IForgeClient forge, string owner, string repo,
        [Description("Include anonymous (email-only, unlinked) contributors. Default false.")] bool anon = false,
        [Description("Max results (default 100).")] int limit = 100,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.ListContributorsAsync(owner, repo, anon, limit, ct));

    [McpServerTool(Name = "get_contributor_stats", ReadOnly = true)]
    [Description("Per-contributor weekly additions/deletions/commits for the last year. Computed asynchronously: may return a 202 retry envelope on the first call.")]
    public static Task<object> GetContributorStats(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetContributorStatsAsync(owner, repo, ct));

    [McpServerTool(Name = "get_commit_activity", ReadOnly = true)]
    [Description("Commit counts per day, grouped into the last 52 weeks. Computed asynchronously: may return a 202 retry envelope on the first call.")]
    public static Task<object> GetCommitActivity(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetCommitActivityAsync(owner, repo, ct));

    [McpServerTool(Name = "get_code_frequency", ReadOnly = true)]
    [Description("Weekly additions/deletions across the repository's whole history. Computed asynchronously (202 retry envelope on the first call); a repo with too many commits returns a 422 envelope.")]
    public static Task<object> GetCodeFrequency(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetCodeFrequencyAsync(owner, repo, ct));

    [McpServerTool(Name = "get_participation", ReadOnly = true)]
    [Description("Weekly commit counts for the last 52 weeks, split into all contributors vs the repo owner. Computed asynchronously: may return a 202 retry envelope.")]
    public static Task<object> GetParticipation(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetParticipationAsync(owner, repo, ct));

    [McpServerTool(Name = "get_punch_card", ReadOnly = true)]
    [Description("Commit counts per hour of each day of the week (the day/hour \"punch card\"). Computed asynchronously: may return a 202 retry envelope.")]
    public static Task<object> GetPunchCard(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetPunchCardAsync(owner, repo, ct));

    // ── Community / dependencies ──────────────────────────────────────────────

    [McpServerTool(Name = "get_community_profile", ReadOnly = true)]
    [Description("Community health metrics: which of README / LICENSE / CONTRIBUTING / CODE_OF_CONDUCT / issue + PR templates exist, plus the overall health percentage.")]
    public static Task<object> GetCommunityProfile(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetCommunityProfileAsync(owner, repo, ct));

    [McpServerTool(Name = "get_sbom", ReadOnly = true)]
    [Description("The repository's dependency-graph SBOM (SPDX): document metadata plus the resolved package list.")]
    public static Task<object> GetSbom(IForgeClient forge, string owner, string repo,
        [Description("Max packages returned (default 200); the full count is always reported.")] int limit = 200,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.GetSbomAsync(owner, repo, limit, ct));

    [McpServerTool(Name = "list_forks", ReadOnly = true)]
    [Description("List the forks of a repository.")]
    public static Task<object> ListForks(IForgeClient forge, string owner, string repo,
        [Description("Sort order: \"newest\" (default), \"oldest\", \"stargazers\", or \"watchers\".")] string sort = "newest",
        [Description("Max results (default 30).")] int limit = 30,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateChoice(sort, "sort", ForkSortValues)
                  ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.ListForksAsync(owner, repo, sort, limit, ct));

    [McpServerTool(Name = "get_languages", ReadOnly = true)]
    [Description("Language breakdown for a repository, in bytes of source per language, with the total.")]
    public static Task<object> GetLanguages(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetLanguagesAsync(owner, repo, ct));
}
