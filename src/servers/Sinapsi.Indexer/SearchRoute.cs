// ---------------------------------------------------------------------------
// SearchRoute - request/response DTOs for the HTTP GET /search endpoint.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

namespace Sinapsi.Indexer;

/// <summary>
/// Parsed, validated query parameters for <c>GET /search</c>.
/// Constructed by <see cref="SearchRequest.TryParse"/>; never allocated when
/// validation fails (the caller returns 400 directly).
/// </summary>
public sealed record SearchRequest(string Query, int Limit, string? Source)
{
    /// <summary>
    /// Parse + validate the raw query-string values. Returns a structured error
    /// string (non-null) when validation fails — the caller maps that to a 400.
    /// </summary>
    public static (SearchRequest? req, string? error) TryParse(string? q, string? limitRaw, string? source)
    {
        if (IndexerValidation.ValidateQuery(q) is { } qErr)
            return (null, qErr);

        var limit = 10;
        if (!string.IsNullOrEmpty(limitRaw))
        {
            if (!int.TryParse(limitRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out limit))
                return (null, $"limit '{limitRaw}' is not a valid integer");
            if (IndexerValidation.ValidateLimit(limit) is { } lErr)
                return (null, lErr);
        }

        if (IndexerValidation.ValidateFilterToken("source", source) is { } sErr)
            return (null, sErr);

        return (new SearchRequest(q!, limit, source), null);
    }
}

/// <summary>
/// One ranked hit in the <c>GET /search</c> response — a subset of
/// <see cref="SearchHit"/> shaped for JSON serialisation.
/// </summary>
public sealed record SearchResultItem(
    string Source,
    string Path,
    string Kind,
    string Title,
    string Scope,
    string Snippet,
    double Score);

/// <summary>
/// The full JSON body of a successful <c>GET /search</c> response.
/// </summary>
public sealed record SearchResponse(
    string Query,
    int ResultCount,
    IReadOnlyList<SearchResultItem> Results);
