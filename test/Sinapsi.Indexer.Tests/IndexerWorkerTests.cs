using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// The event-driven re-index half (the capability the prior port DROPPED, now
/// restored). The worker consumes git-push notifications and marks the pushed
/// repo dirty; a coalescing loop re-scans it. These pin the subject→repo parsing,
/// which is prefix-agnostic (env-driven watch subject) and tolerant of arbitrary
/// branch tokens.
/// </summary>
public sealed class IndexerWorkerTests
{
    [Theory]
    [InlineData("events.git.docs.push.main", "docs")]
    [InlineData("myorg.git.kb-repo.push.main", "kb-repo")]
    [InlineData("acme.events.git.learnings.push.feature/x", "learnings")]
    public void RepoNameFromSubject_extracts_the_repo_after_the_git_segment(string subject, string expected)
    {
        Assert.Equal(expected, IndexerWorker.RepoNameFromSubject(subject));
    }

    [Theory]
    [InlineData("events.notgit.docs.push.main")]   // no "git" segment
    [InlineData("git")]                             // "git" with nothing after it
    [InlineData("events.git")]                      // trailing "git", no repo token
    public void RepoNameFromSubject_returns_null_for_a_non_git_push_subject(string subject)
    {
        Assert.Null(IndexerWorker.RepoNameFromSubject(subject));
    }
}
