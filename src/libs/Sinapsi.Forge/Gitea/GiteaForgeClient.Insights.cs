namespace Sinapsi.Forge.Gitea;

/// <summary>
/// Repository <b>Insights</b> — GitHub-only. Gitea/Forgejo/Codeberg expose no traffic,
/// <c>stats/*</c>, community-profile, or dependency-graph SBOM API, so every method here
/// throws <see cref="ForgeNotSupportedException"/>, the same shape the GitHub adapter uses
/// for the Gitea-only time-tracking surface. <c>InsightsTools</c> is registered by the
/// <c>Github.Mcp</c> host only, so this is a contract-honesty backstop rather than a live
/// path; the guard turns it into an <c>{ ok:false, status:null, error }</c> envelope if a
/// host ever wires it up by mistake.
/// </summary>
public sealed partial class GiteaForgeClient
{
    private const string NotSupported =
        "Repository insights (traffic / commit + contributor stats / community profile / SBOM) are a GitHub feature; Gitea, Forgejo and Codeberg have no equivalent.";

    public Task<object> GetTrafficViewsAsync(string owner, string repo, string per, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetTrafficClonesAsync(string owner, string repo, string per, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetTrafficReferrersAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetTrafficPathsAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> ListContributorsAsync(string owner, string repo, bool anon, int limit, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetContributorStatsAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetCommitActivityAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetCodeFrequencyAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetParticipationAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetPunchCardAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetCommunityProfileAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetSbomAsync(string owner, string repo, int limit, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> ListForksAsync(string owner, string repo, string sort, int limit, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);

    public Task<object> GetLanguagesAsync(string owner, string repo, CancellationToken ct = default)
        => throw new ForgeNotSupportedException(NotSupported);
}
