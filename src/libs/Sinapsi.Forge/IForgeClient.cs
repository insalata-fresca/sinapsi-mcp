using Sinapsi.Forge.Model;

namespace Sinapsi.Forge;

/// <summary>
/// Provider-neutral git-forge operations. Implemented by <c>GiteaForgeClient</c>
/// (Forgejo + Codeberg) and <c>GitHubForgeClient</c> (raw REST, in the Github.Mcp
/// server). The common <c>[McpServerTool]</c> classes call this; the host registers
/// the concrete adapter as the DI singleton.
///
/// Grows by domain. Every method an adapter cannot support throws
/// <see cref="ForgeNotSupportedException"/> (surfaced as a tool error), gated by
/// <see cref="Capabilities"/>.
/// </summary>
public interface IForgeClient
{
    /// <summary>Which provider this client targets (for diagnostics + capability gating).</summary>
    ForgeProvider Provider { get; }

    /// <summary>Capability flags — tools check these before calling provider-specific ops.</summary>
    ForgeCapabilities Capabilities { get; }

    // ── Users ────────────────────────────────────────────────────────────────
    Task<ForgeUser> GetMeAsync(CancellationToken ct = default);
    Task<ForgeUser> GetUserAsync(string username, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeUser>> SearchUsersAsync(string query, int limit = 30, CancellationToken ct = default);

    // ── Repositories ───────────────────────────────────────────────────────────
    Task<ForgeRepo> GetRepoAsync(string owner, string repo, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeRepo>> ListMyReposAsync(int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeRepo>> SearchReposAsync(string query, int limit = 30, CancellationToken ct = default);
    Task<ForgeRepo> CreateRepoAsync(CreateRepoRequest req, CancellationToken ct = default);
    Task<ForgeRepo> ForkRepoAsync(string owner, string repo, string? organization, CancellationToken ct = default);
    Task<ForgeRepo> EditRepoAsync(string owner, string repo, EditRepoRequest req, CancellationToken ct = default);
    Task DeleteRepoAsync(string owner, string repo, CancellationToken ct = default);

    // ── Contents / files ───────────────────────────────────────────────────────
    Task<ForgeContentListing> GetContentsAsync(string owner, string repo, string path, string? gitRef, CancellationToken ct = default);
    Task<ForgeBinary> GetFileBinaryAsync(string owner, string repo, string path, string? gitRef, CancellationToken ct = default);
    Task<ForgeCommitResult> CreateOrUpdateFileAsync(string owner, string repo, string path, string contentBase64, string message, string branch, string? sha, CancellationToken ct = default);
    Task<ForgeCommitResult> DeleteFileAsync(string owner, string repo, string path, string message, string branch, string sha, CancellationToken ct = default);
    Task<ForgeCommitResult> CommitFilesAsync(string owner, string repo, string branch, string? newBranch, string message, IReadOnlyList<ForgeFileChange> files, CancellationToken ct = default);

    // ── Branches ─────────────────────────────────────────────────────────────
    Task<IReadOnlyList<ForgeBranch>> ListBranchesAsync(string owner, string repo, int limit = 50, CancellationToken ct = default);
    Task<ForgeBranch> GetBranchAsync(string owner, string repo, string branch, CancellationToken ct = default);
    Task<ForgeBranch> CreateBranchAsync(string owner, string repo, string newBranch, string? fromBranch, CancellationToken ct = default);
    Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct = default);

    // ── Commits ──────────────────────────────────────────────────────────────
    Task<IReadOnlyList<ForgeCommit>> ListCommitsAsync(string owner, string repo, string? sha, int limit = 30, CancellationToken ct = default);
    Task<ForgeCommit> GetCommitAsync(string owner, string repo, string sha, CancellationToken ct = default);

    // ── Issues ─────────────────────────────────────────────────────────────────
    Task<ForgeIssue> CreateIssueAsync(string owner, string repo, CreateIssueRequest req, CancellationToken ct = default);
    Task<ForgeIssue> GetIssueAsync(string owner, string repo, long number, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeIssue>> ListIssuesAsync(string owner, string repo, string? state, string? labels, int limit = 30, CancellationToken ct = default);
    Task<ForgeIssue> UpdateIssueAsync(string owner, string repo, long number, UpdateIssueRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeComment>> ListIssueCommentsAsync(string owner, string repo, long number, CancellationToken ct = default);
    Task<ForgeComment> CreateIssueCommentAsync(string owner, string repo, long number, string body, CancellationToken ct = default);
    Task<ForgeComment> EditIssueCommentAsync(string owner, string repo, long commentId, string body, CancellationToken ct = default);
    Task DeleteIssueCommentAsync(string owner, string repo, long commentId, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeLabel>> ListRepoLabelsAsync(string owner, string repo, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeLabel>> AddIssueLabelsAsync(string owner, string repo, long number, IReadOnlyList<long> labelIds, CancellationToken ct = default);
    Task RemoveIssueLabelAsync(string owner, string repo, long number, long labelId, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeMilestone>> ListMilestonesAsync(string owner, string repo, string? state, CancellationToken ct = default);

    // ── Pull requests ──────────────────────────────────────────────────────────
    Task<ForgePullRequest> CreatePullRequestAsync(string owner, string repo, CreatePullRequest req, CancellationToken ct = default);
    Task<ForgePullRequest> GetPullRequestAsync(string owner, string repo, long number, CancellationToken ct = default);
    Task<IReadOnlyList<ForgePullRequest>> ListPullRequestsAsync(string owner, string repo, string? state, int limit = 30, CancellationToken ct = default);
    Task<ForgePullRequest> UpdatePullRequestAsync(string owner, string repo, long number, UpdatePullRequest req, CancellationToken ct = default);
    Task<ForgeMergeResult> MergePullRequestAsync(string owner, string repo, long number, string method, string? title, string? message, bool? deleteBranchAfterMerge = null, CancellationToken ct = default);
    Task<IReadOnlyList<ForgePullFile>> ListPullRequestFilesAsync(string owner, string repo, long number, CancellationToken ct = default);
    Task<string> GetPullRequestDiffAsync(string owner, string repo, long number, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeReview>> ListPullReviewsAsync(string owner, string repo, long number, CancellationToken ct = default);
    Task<ForgeReview> CreatePullReviewAsync(string owner, string repo, long number, string @event, string? body, CancellationToken ct = default);
    Task RequestReviewersAsync(string owner, string repo, long number, IReadOnlyList<string> reviewers, bool remove, CancellationToken ct = default);

    // ── Releases & tags ──────────────────────────────────────────────────────
    Task<IReadOnlyList<ForgeRelease>> ListReleasesAsync(string owner, string repo, int limit = 30, CancellationToken ct = default);
    Task<ForgeRelease> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct = default);
    Task<ForgeRelease> CreateReleaseAsync(string owner, string repo, CreateReleaseRequest req, CancellationToken ct = default);
    Task<ForgeReleaseAsset> UploadReleaseAssetAsync(string owner, string repo, long releaseId, string name, string? contentBase64, string? sourcePath = null, string? sourceUrl = null, CancellationToken ct = default);
    Task<ForgeRelease> GetReleaseAsync(string owner, string repo, long releaseId, CancellationToken ct = default);
    Task<ForgeRelease> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken ct = default);
    Task<ForgeRelease> EditReleaseAsync(string owner, string repo, long releaseId, EditReleaseRequest req, CancellationToken ct = default);
    Task DeleteReleaseAsync(string owner, string repo, long releaseId, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeReleaseAsset>> ListReleaseAssetsAsync(string owner, string repo, long releaseId, CancellationToken ct = default);
    Task<ForgeReleaseAsset> EditReleaseAssetAsync(string owner, string repo, long releaseId, long assetId, string name, CancellationToken ct = default);
    Task DeleteReleaseAssetAsync(string owner, string repo, long releaseId, long assetId, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeTag>> ListTagsAsync(string owner, string repo, int limit = 30, CancellationToken ct = default);

    // ── Search ─────────────────────────────────────────────────────────────────
    Task<IReadOnlyList<ForgeIssue>> SearchIssuesAsync(string query, string type, int limit = 30, CancellationToken ct = default); // type: issues | pulls

    // ── Orgs & teams ───────────────────────────────────────────────────────────
    Task<ForgeOrg> GetOrgAsync(string org, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeOrg>> ListMyOrgsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ForgeOrg>> ListUserOrgsAsync(string username, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListOrgMembersAsync(string org, CancellationToken ct = default);
    Task<bool> CheckOrgMembershipAsync(string org, string username, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeTeam>> ListOrgTeamsAsync(string org, CancellationToken ct = default);

    // ── Notifications ──────────────────────────────────────────────────────────
    Task<IReadOnlyList<ForgeNotification>> ListNotificationsAsync(bool all, int limit, CancellationToken ct = default);
    Task MarkNotificationReadAsync(long id, CancellationToken ct = default);
    Task MarkAllNotificationsReadAsync(CancellationToken ct = default);

    // ── Webhooks ─────────────────────────────────────────────────────────────
    Task<IReadOnlyList<ForgeWebhook>> ListWebhooksAsync(string owner, string repo, CancellationToken ct = default);
    Task<ForgeWebhook> CreateWebhookAsync(string owner, string repo, string url, IReadOnlyList<string> events, string? secret, string contentType, CancellationToken ct = default);
    Task DeleteWebhookAsync(string owner, string repo, long id, CancellationToken ct = default);

    // ── Time tracking (Gitea-only) ─────────────────────────────────────────────
    Task<IReadOnlyList<ForgeTrackedTime>> ListIssueTrackedTimesAsync(string owner, string repo, long number, CancellationToken ct = default);
    Task<ForgeTrackedTime> AddIssueTimeAsync(string owner, string repo, long number, long seconds, CancellationToken ct = default);

    // ── Actions / CI workflows ─────────────────────────────────────────────────
    Task<ForgeWorkflowDispatchResult> DispatchWorkflowAsync(string owner, string repo, string workflow, string gitRef, IReadOnlyDictionary<string, string>? inputs, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeWorkflowRun>> ListWorkflowRunsAsync(string owner, string repo, string? workflow, int limit = 30, CancellationToken ct = default);

    // ── Repository topics ──────────────────────────────────────────────────────
    // Add/remove/set return the resulting topic list so the caller sees the live set.
    Task<IReadOnlyList<string>> ListRepoTopicsAsync(string owner, string repo, CancellationToken ct = default);
    Task<IReadOnlyList<string>> AddRepoTopicAsync(string owner, string repo, string topic, CancellationToken ct = default);
    Task<IReadOnlyList<string>> RemoveRepoTopicAsync(string owner, string repo, string topic, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SetRepoTopicsAsync(string owner, string repo, IReadOnlyList<string> topics, CancellationToken ct = default);

    // ── Insights (GitHub-only; gated by ForgeCapabilities.Insights) ────────────
    // The repository "Insights" surface: traffic, contributor/commit statistics,
    // the community profile, the dependency-graph SBOM, forks, and languages.
    //
    // These return <c>object</c> rather than a typed record on purpose: each one is a
    // UNION of a mapped payload and a structured NON-ERROR envelope the caller must be
    // able to read as an answer —
    //   • <c>{ ok:false, status:202, retry:true, note }</c> — GitHub is still computing a
    //     <c>stats/*</c> cache and answered 202 with an empty body (see the adapter);
    //   • <c>{ ok:false, status:403, note }</c> — <c>traffic/*</c> needs push access;
    //   • <c>{ ok:false, status:422, note }</c> — the repo is too large for code frequency.
    // A 404 (and every other non-2xx) stays on the normal ForgeApiException path.
    Task<object> GetTrafficViewsAsync(string owner, string repo, string per, CancellationToken ct = default);
    Task<object> GetTrafficClonesAsync(string owner, string repo, string per, CancellationToken ct = default);
    Task<object> GetTrafficReferrersAsync(string owner, string repo, CancellationToken ct = default);
    Task<object> GetTrafficPathsAsync(string owner, string repo, CancellationToken ct = default);
    Task<object> ListContributorsAsync(string owner, string repo, bool anon, int limit, CancellationToken ct = default);
    Task<object> GetContributorStatsAsync(string owner, string repo, CancellationToken ct = default);
    Task<object> GetCommitActivityAsync(string owner, string repo, CancellationToken ct = default);
    Task<object> GetCodeFrequencyAsync(string owner, string repo, CancellationToken ct = default);
    Task<object> GetParticipationAsync(string owner, string repo, CancellationToken ct = default);
    Task<object> GetPunchCardAsync(string owner, string repo, CancellationToken ct = default);
    Task<object> GetCommunityProfileAsync(string owner, string repo, CancellationToken ct = default);
    Task<object> GetSbomAsync(string owner, string repo, int limit, CancellationToken ct = default);
    Task<object> ListForksAsync(string owner, string repo, string sort, int limit, CancellationToken ct = default);
    Task<object> GetLanguagesAsync(string owner, string repo, CancellationToken ct = default);
}

public enum ForgeProvider { Gitea, GitHub }

public sealed record CreateRepoRequest(
    string Name,
    string? Owner = null,        // null = the authenticated user; else an org
    string? Description = null,
    bool Private = true,
    bool AutoInit = false,
    string? DefaultBranch = null,
    string? License = null,
    string? Gitignores = null,
    string? Readme = null);

public sealed record EditRepoRequest(
    string? Description = null,
    bool? Private = null,
    string? DefaultBranch = null,
    bool? HasIssues = null,
    bool? HasWiki = null,
    bool? Archived = null,
    bool? HasReleases = null,
    /// <summary>Delete the head branch automatically when a pull request is merged.
    /// Forgejo/Gitea field <c>default_delete_branch_after_merge</c>; GitHub calls it
    /// <c>delete_branch_on_merge</c> — each adapter maps to its own name.</summary>
    bool? DefaultDeleteBranchAfterMerge = null,
    /// <summary>The repository's homepage / website URL. Forgejo/Gitea field <c>website</c>;
    /// GitHub calls it <c>homepage</c> — each adapter maps to its own name. Pass an empty
    /// string to clear it; omit (null) to leave the current value untouched.</summary>
    string? Homepage = null);

public sealed record CreateIssueRequest(
    string Title,
    string? Body = null,
    IReadOnlyList<string>? Assignees = null,
    IReadOnlyList<long>? Labels = null,
    long? Milestone = null);

public sealed record UpdateIssueRequest(
    string? Title = null,
    string? Body = null,
    string? State = null,           // "open" | "closed"
    IReadOnlyList<string>? Assignees = null,
    long? Milestone = null);

public sealed record CreatePullRequest(
    string Title,
    string Head,
    string Base,
    string? Body = null,
    IReadOnlyList<string>? Assignees = null);

public sealed record UpdatePullRequest(
    string? Title = null,
    string? Body = null,
    string? State = null,
    string? Base = null);

public sealed record CreateReleaseRequest(
    string TagName,
    string? Name = null,
    string? Body = null,
    string? TargetCommitish = null,
    bool Draft = false,
    bool Prerelease = false);

public sealed record EditReleaseRequest(
    string? TagName = null,
    string? TargetCommitish = null,
    string? Name = null,
    string? Body = null,
    bool? Draft = null,
    bool? Prerelease = null);
