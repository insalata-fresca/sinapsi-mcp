namespace Sinapsi.Forge.Model;

// Provider-neutral DTOs returned by IForgeClient. Shapes are the common subset of the
// GitHub + Gitea REST APIs; adapters map their native payloads onto these. Records are
// serialised back to the MCP caller as JSON.

public sealed record ForgeUser(
    string Login,
    long Id,
    string? FullName,
    string? Email,
    string? AvatarUrl,
    string? HtmlUrl,
    bool? IsAdmin);

public sealed record ForgeRepo(
    string Owner,
    string Name,
    string FullName,
    bool Private,
    bool Fork,
    string? Description,
    string DefaultBranch,
    string? CloneUrl,
    string? SshUrl,
    string? HtmlUrl,
    long? Stars,
    long? Forks,
    long? OpenIssues,
    /// <summary>Repository homepage / website URL — Forgejo/Gitea <c>website</c>, GitHub
    /// <c>homepage</c>. Appended with a default so existing positional construction still
    /// compiles; both adapters populate it by name.</summary>
    string? Homepage = null);

public sealed record ForgeBranch(
    string Name,
    string CommitSha,
    bool? Protected);

public sealed record ForgeCommitAuthor(string? Name, string? Email, DateTimeOffset? Date, string? DateRaw = null);

public sealed record ForgeCommit(
    string Sha,
    string Message,
    ForgeCommitAuthor? Author,
    string? HtmlUrl);

public sealed record ForgeFile(
    string Path,
    string? Sha,
    long? Size,
    string Type,            // "file" | "dir" | "symlink" | "submodule"
    string? Encoding,       // "base64" when Content is populated for a file
    string? Content,        // text or base64 per Encoding
    string? HtmlUrl,
    string? DownloadUrl);

public sealed record ForgeDirEntry(string Name, string Path, string Type, long? Size, string? Sha);

public sealed record ForgeContentListing(
    string Path,
    string Type,            // "file" | "dir"
    ForgeFile? File,
    IReadOnlyList<ForgeDirEntry>? Entries);

// One file mutation inside a commit_files (ChangeFiles) call.
public sealed record ForgeFileChange(
    string Path,
    string Operation,        // "create" | "update" | "delete"
    string? ContentBase64,   // create/update: the EXACT bytes (binary-safe)
    string? Sha,             // required for update/delete (current blob sha)
    string? FromPath,        // optional rename source (update)
    string? Content = null); // create/update: RAW UTF-8 text — server base64-encodes it (alternative to ContentBase64)

public sealed record ForgeCommitResult(
    string CommitSha,
    string Branch,
    string? HtmlUrl,
    IReadOnlyList<string> Paths);

public sealed record ForgeBinary(
    string Path,
    string? Sha,
    long Size,
    string MimeTypeGuess,
    string ContentBase64);

public sealed record ForgeLabel(long Id, string Name, string? Color, string? Description);

public sealed record ForgeMilestone(long Id, string Title, string? State, string? Description, long? OpenIssues, long? ClosedIssues);

public sealed record ForgeIssue(
    long Number,
    string Title,
    string? Body,
    string State,
    string? AuthorLogin,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<string> Labels,
    long? CommentCount,
    string? HtmlUrl);

public sealed record ForgeComment(
    long Id,
    string? Body,
    string? AuthorLogin,
    DateTimeOffset? CreatedAt,
    string? HtmlUrl);

public sealed record ForgePullRequest(
    long Number,
    string Title,
    string? Body,
    string State,
    bool? Merged,
    bool? Mergeable,
    string? HeadRef,
    string? BaseRef,
    string? AuthorLogin,
    string? HtmlUrl);

public sealed record ForgePullFile(string Path, string? Status, long? Additions, long? Deletions, long? Changes);

public sealed record ForgeReview(
    long Id,
    string? State,
    string? Body,
    string? ReviewerLogin,
    DateTimeOffset? SubmittedAt,
    string? HtmlUrl);

public sealed record ForgeMergeResult(long Number, bool Merged, string? Message);

public sealed record ForgeReleaseAsset(long Id, string Name, long? Size, string? DownloadUrl);

public sealed record ForgeRelease(
    long Id,
    string TagName,
    string? Name,
    string? Body,
    bool Draft,
    bool Prerelease,
    string? HtmlUrl,
    IReadOnlyList<ForgeReleaseAsset> Assets);

public sealed record ForgeTag(string Name, string? CommitSha);

public sealed record ForgeOrg(string Login, long Id, string? FullName, string? Description, string? HtmlUrl);

public sealed record ForgeTeam(long Id, string Name, string? Permission, string? Description);

public sealed record ForgeNotification(
    long Id,
    string? Type,
    string? Title,
    string? State,
    bool? Unread,
    string? SubjectUrl);

public sealed record ForgeWebhook(long Id, string? Type, bool? Active, IReadOnlyList<string> Events, string? Url);

public sealed record ForgeTrackedTime(long Id, long? IssueNumber, string? UserLogin, long Seconds, DateTimeOffset? Created);

// ── Actions / CI workflows ──────────────────────────────────────────────────
public sealed record ForgeWorkflowDispatchResult(
    bool Dispatched,
    string Workflow,
    string Ref,
    string? Message);

public sealed record ForgeWorkflowRun(
    long Id,
    long? RunNumber,        // Gitea index_in_repo / GitHub run_number
    string? WorkflowId,     // workflow file name (Gitea) / workflow name (GitHub)
    string? Title,
    string Status,          // queued | in_progress | completed | success | failure | …
    string? Conclusion,     // GitHub: success|failure|cancelled|… ; Gitea: null (status carries it)
    string? Event,          // trigger event (push, workflow_dispatch, …)
    string? HeadSha,        // commit sha the run ran on
    string? HeadBranch,     // ref / branch
    string? HtmlUrl,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);
