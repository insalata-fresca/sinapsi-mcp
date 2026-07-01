// ---------------------------------------------------------------------------
// IndexerValidation - fail-fast input validation for the indexer MCP tools.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

namespace Sinapsi.Indexer;

/// <summary>
/// Input validation for the indexer MCP tool surface. Every user-supplied
/// parameter that flows into a SQL query, an FTS query, or a NATS subject token
/// is checked here BEFORE any side effect (DB round-trip, embedding, or event
/// publish), so malformed input is rejected with a clear, structured reason
/// instead of reaching the data tier or the bus.
///
/// <para>
/// Note on injection: all SQL is parameterised (Npgsql <c>@</c> parameters) and
/// the NATS subject tokens are separately guarded by a kebab-case regex in
/// <see cref="LearnTools"/>, so this validation is a correctness + fail-fast
/// guard and a defence-in-depth layer, not the sole injection defence. Every
/// method returns <c>null</c> when the value is acceptable, otherwise a
/// human-readable reason; none of them throw.
/// </para>
/// </summary>
internal static class IndexerValidation
{
    /// <summary>Upper bound on a free-text search / FTS query string. A websearch
    /// query is a handful of words + operators; 2 KiB is far past any legitimate
    /// query yet refuses an unbounded blob that would waste an FTS parse.</summary>
    internal const int MaxQueryLength = 2_000;

    /// <summary>Upper bound on a <c>source</c> / <c>scope</c> filter token. These
    /// map to a logical repo/bucket name; 128 is generous.</summary>
    internal const int MaxFilterLength = 128;

    /// <summary>Upper bound on a learning <c>slug</c> (the entry id + NATS subject
    /// token). Kept short: it becomes a filename and a subject segment.</summary>
    internal const int MaxSlugLength = 128;

    /// <summary>Upper bound on a learning <c>title</c> (a one-line summary).</summary>
    internal const int MaxTitleLength = 300;

    /// <summary>Upper bound on a learning <c>body</c> (full markdown). 256 KiB is
    /// generous for a single learning entry while still refusing a runaway paste.</summary>
    internal const int MaxBodyLength = 262_144;

    /// <summary>Upper bound on a single tag / the session-context line.</summary>
    internal const int MaxTagLength = 64;

    /// <summary>Maximum number of tags accepted on one learning.</summary>
    internal const int MaxTags = 32;

    /// <summary>Upper bound on the free-text session-context line.</summary>
    internal const int MaxSessionContextLength = 300;

    /// <summary>Hard ceiling on a caller-requested result <c>limit</c>. The store
    /// already clamps to this; validating here rejects an obviously-bogus value
    /// (negative / absurd) with a clear message rather than silently clamping.</summary>
    internal const int MaxLimit = 30;

    /// <summary>The closed set of document kinds the classifier emits — the only
    /// values a <c>kind</c> filter may legitimately carry.</summary>
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "doc", "pattern", "convention", "decision", "caveat",
        "scope", "state", "learning", "backlog",
    };

    /// <summary>
    /// Validate a required full-text query (<c>search_index</c> / <c>semantic_search</c>).
    /// Returns <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "query is required";
        if (query.Length > MaxQueryLength)
            return $"query too long ({query.Length} chars; max {MaxQueryLength})";
        if (ContainsControlOrNewline(query))
            return "query contains control characters";
        return null;
    }

    /// <summary>
    /// Validate an optional full-text query (<c>get_learning</c>, where a null
    /// query lists the scope). Returns <c>null</c> when valid (including when
    /// null/empty), otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateOptionalQuery(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return null;
        if (query.Length > MaxQueryLength)
            return $"query too long ({query.Length} chars; max {MaxQueryLength})";
        if (ContainsControlOrNewline(query))
            return "query contains control characters";
        return null;
    }

    /// <summary>
    /// Validate an optional <c>source</c> or <c>scope</c> filter (a logical repo /
    /// bucket name). Null/empty means "no filter" and is accepted. Returns
    /// <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateFilterToken(string paramName, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.Length > MaxFilterLength)
            return $"{paramName} too long ({value.Length} chars; max {MaxFilterLength})";
        if (ContainsControlOrNewline(value))
            return $"{paramName} contains control characters";
        return null;
    }

    /// <summary>
    /// Validate an optional <c>kind</c> filter. Null/empty means "no filter". A
    /// non-empty value must be one of the classifier's known kinds. Returns
    /// <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateKind(string? kind)
    {
        if (string.IsNullOrEmpty(kind))
            return null;
        if (!Kinds.Contains(kind))
            return $"kind '{Truncate(kind, 40)}' is not one of: {string.Join('|', Kinds.OrderBy(k => k))}";
        return null;
    }

    /// <summary>
    /// Validate a caller-requested result <c>limit</c>. Must be positive; an
    /// absurd value is rejected (the store clamps to <see cref="MaxLimit"/>, but a
    /// negative or wildly-large request is a caller bug worth surfacing). Returns
    /// <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateLimit(int limit)
    {
        if (limit <= 0)
            return $"limit must be positive (got {limit})";
        if (limit > MaxLimit)
            return $"limit {limit} exceeds max {MaxLimit}";
        return null;
    }

    /// <summary>
    /// Validate a learning <c>slug</c>. It becomes the entry id AND a NATS subject
    /// token, so beyond the kebab-case check <see cref="LearnTools"/> applies, we
    /// cap its length here. Returns <c>null</c> when valid, otherwise a reason.
    /// </summary>
    internal static string? ValidateSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return "slug is required";
        if (slug.Length > MaxSlugLength)
            return $"slug too long ({slug.Length} chars; max {MaxSlugLength})";
        return null;
    }

    /// <summary>
    /// Validate a learning <c>title</c> (required, one-line). Returns <c>null</c>
    /// when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "title is required";
        if (title.Length > MaxTitleLength)
            return $"title too long ({title.Length} chars; max {MaxTitleLength})";
        if (ContainsControlOrNewline(title))
            return "title contains control characters or newlines (it is a one-line summary)";
        return null;
    }

    /// <summary>
    /// Validate a learning <c>body</c> (required markdown). Newlines ARE allowed
    /// (it is multi-line markdown); other control characters and an oversize blob
    /// are not. Returns <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "body is required";
        if (body.Length > MaxBodyLength)
            return $"body too long ({body.Length} chars; max {MaxBodyLength})";
        if (ContainsControlExceptNewline(body))
            return "body contains control characters";
        return null;
    }

    /// <summary>
    /// Validate the optional tag list. Null/empty is accepted. Each tag is
    /// capped; the list is bounded. Returns <c>null</c> when valid, otherwise a
    /// human-readable reason.
    /// </summary>
    internal static string? ValidateTags(string[]? tags)
    {
        if (tags is null || tags.Length == 0)
            return null;
        if (tags.Length > MaxTags)
            return $"too many tags ({tags.Length}; max {MaxTags})";
        for (var i = 0; i < tags.Length; i++)
        {
            var t = tags[i];
            if (t is null) continue; // blank tags are dropped downstream
            if (t.Length > MaxTagLength)
                return $"tag #{i + 1} too long ({t.Length} chars; max {MaxTagLength})";
            if (ContainsControlOrNewline(t))
                return $"tag #{i + 1} contains control characters";
        }
        return null;
    }

    /// <summary>
    /// Validate the optional one-line session-context. Null/empty is accepted.
    /// Returns <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateSessionContext(string? sessionContext)
    {
        if (string.IsNullOrEmpty(sessionContext))
            return null;
        if (sessionContext.Length > MaxSessionContextLength)
            return $"session_context too long ({sessionContext.Length} chars; max {MaxSessionContextLength})";
        if (ContainsControlOrNewline(sessionContext))
            return "session_context contains control characters or newlines (it is a one-line note)";
        return null;
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }

    private static bool ContainsControlExceptNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) return true;
        return false;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
