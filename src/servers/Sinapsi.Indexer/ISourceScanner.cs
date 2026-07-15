// ---------------------------------------------------------------------------
// ISourceScanner - the SOURCE-side seam of the indexer.
//
// IndexerCore (+ the two worker shells) drives "re-scan the sources into
// Documents" through this interface, NOT a concrete scanner, so a future
// non-git source (e.g. an OPDS catalogue that polls + downloads + extracts
// rather than git-clones) can slot in behind the same seam without touching
// IndexerCore. Extracted from the concrete SourceScanner in M1 as a PURE
// refactor — GitSourceScanner is the sole implementation and behaves
// byte-identically to the pre-refactor class.
//
// Git-neutrality: the interface deliberately does NOT expose any git-specific
// type (clone URL / branch / cache dir). A source is identified to the core by
// its LOGICAL name only (ISourceRef.Source) — the token the workers match a
// git-push subject against. Everything git-shaped (RepoSpec's Url/Branch/
// CacheDir) stays an implementation detail of GitSourceScanner.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

namespace Sinapsi.Indexer;

/// <summary>
/// A source-neutral handle to ONE source the indexer tracks. The only thing the
/// core/workers ever read off a source is its <see cref="Source"/> logical name
/// (used to match a git-push subject to a tracked source and to key Documents);
/// every source-type-specific detail (a git clone URL/branch/cache dir, an OPDS
/// feed URL, …) lives on the concrete implementation and never crosses this
/// seam. <c>RepoSpec</c> implements this for the git source.
/// </summary>
public interface ISourceRef
{
    /// <summary>The logical source name (e.g. "docs", "learnings"). Stable
    /// identity used to match push notifications and to key <see cref="Document"/>s.</summary>
    string Source { get; }
}

/// <summary>
/// The SOURCE seam: (re)scan the source(s) of truth into <see cref="Document"/>s.
/// A rescan is sync (pull/refresh) + scan (walk → classify → hash), never an
/// event-log replay. Implemented today only by <see cref="GitSourceScanner"/>
/// (git-clone + markdown walk); shaped so a future OPDS/download source can
/// implement the same three operations without leaking its transport into the
/// core's contract.
/// </summary>
public interface ISourceScanner
{
    /// <summary>The sources this scanner tracks, as source-neutral handles.
    /// The core iterates these to rescan-all and the workers match a push
    /// subject's repo token against each handle's <see cref="ISourceRef.Source"/>.</summary>
    IReadOnlyList<ISourceRef> Sources { get; }

    /// <summary>Refresh ONE source to its latest state (git: clone-or-fetch+reset;
    /// a future source: poll/download). Returns false on failure — the caller
    /// skips scanning a source that failed to sync rather than indexing stale/empty
    /// content. Must not throw for an ordinary sync failure.</summary>
    Task<bool> SyncAsync(ISourceRef source, CancellationToken ct);

    /// <summary>Walk the synced source into one <see cref="Document"/> per
    /// indexable item (git: per *.md file; a future source: per extracted unit),
    /// applying the source's own denylist / poison-content (NUL byte) skips.</summary>
    IReadOnlyList<Document> Scan(ISourceRef source);
}
