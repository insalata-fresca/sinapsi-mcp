namespace Sinapsi.Indexer;

/// <summary>
/// One indexable source artifact (a markdown file in one of the watched repos).
/// The <see cref="DocId"/> is the stable upsert key ("&lt;source&gt;:&lt;path&gt;");
/// <see cref="ContentSha"/> drives idempotent skip-if-unchanged and change detection.
/// </summary>
public sealed record Document
{
    public required string DocId { get; init; }
    public required string Source { get; init; }   // the logical source name of the repo
    public required string Path { get; init; }      // repo-relative path
    public required string Kind { get; init; }       // see GitSourceScanner.ClassifyKind
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string ContentSha { get; init; }
    /// <summary>For the learnings source: the scope slug parsed from the path
    /// (e.g. "global", a project slug), used by the get_learning tool. Empty otherwise.</summary>
    public string Scope { get; init; } = "";

    /// <summary>
    /// Optional per-document facet metadata, serialised as a JSON object string
    /// (stored in the additive <c>documents.metadata jsonb</c> column — see
    /// <see cref="PostgresIndexStore.EnsureSchemaAsync"/>). Null for every
    /// existing git-source document (shared/career/cervello/learnings) — this
    /// field is ADDITIVE and does not change their behavior. Populated for book
    /// chunks with facets such as isbn/authors/categories/chapter/heading/anchor
    /// (see <see cref="Sinapsi.Opds.BookDocumentMapper"/> in M4's book source).
    /// </summary>
    public string? Metadata { get; init; }

    public static string MakeDocId(string source, string path) => $"{source}:{path}";
}
