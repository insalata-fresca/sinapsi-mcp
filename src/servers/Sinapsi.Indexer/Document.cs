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

    public static string MakeDocId(string source, string path) => $"{source}:{path}";
}
