namespace OpenWrtForum.Mcp;

/// <summary>
/// Input validation for the Discourse-wrapping forum tools. Every tool parameter
/// that flows into an outbound HTTP request — a URL path segment, a query-string
/// value, or a JSON request body — is checked here BEFORE any HTTP call is made,
/// so malformed input is rejected with a clear, structured error instead of being
/// handed to the upstream forum (which would either fail opaquely or waste a
/// round-trip).
///
/// <para>
/// Note on injection safety: query-string values are percent-encoded by
/// <see cref="System.Uri.EscapeDataString"/> in <c>DiscourseClient.BuildUrl</c>,
/// and body values are serialised as JSON, so this validation is a correctness +
/// fail-fast guard, not the sole injection defence. We still reject control
/// characters / newlines everywhere, and — defensively — a leading <c>-</c> on any
/// value that reaches a URL <em>path</em> segment (e.g. <c>category_slug</c>), so a
/// hostile value can never traverse or masquerade as a path component.
/// </para>
///
/// <para>
/// Every method returns <c>null</c> when the value is acceptable, otherwise a
/// short human-readable reason. A <c>null</c>/absent optional parameter is
/// accepted (the tool supplies a default); only malformed <em>present</em> input
/// is rejected.
/// </para>
/// </summary>
internal static class OpenWrtForumValidation
{
    /// <summary>Upper bound on a free-text search query. Discourse itself caps a
    /// query well below this; 500 is a generous ceiling that still refuses an
    /// unbounded blob.</summary>
    internal const int MaxQueryLength = 500;

    /// <summary>Upper bound on a topic title. Discourse's own limit is ~255; we
    /// accept a generous 300 and refuse anything longer before the round-trip.</summary>
    internal const int MaxTitleLength = 300;

    /// <summary>Upper bound on a post/topic markdown body. Discourse accepts long
    /// posts; 64 KiB is a safe ceiling that refuses a pathological megabyte-paste
    /// before it is serialised and sent.</summary>
    internal const int MaxBodyLength = 64 * 1024;

    /// <summary>Upper bound on a category slug (a URL path segment).</summary>
    internal const int MaxSlugLength = 128;

    /// <summary>Upper bound on a single tag string.</summary>
    internal const int MaxTagLength = 100;

    /// <summary>Maximum number of tags accepted on a single create call.</summary>
    internal const int MaxTags = 30;

    /// <summary>Upper bound on a page number. Discourse paginates well within
    /// this; a larger value is a client bug, not a legitimate request.</summary>
    internal const int MaxPage = 1_000;

    /// <summary>Upper bound on a topic / category identifier. IDs are positive
    /// integers; this refuses an obviously-bogus or negative id.</summary>
    internal const int MaxId = int.MaxValue;

    /// <summary>
    /// Validate the free-text <c>query</c> for <c>forum_search</c>. Returns
    /// <c>null</c> when valid, otherwise a human-readable reason.
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
    /// Validate a page number for the paginating read tools. A page is a
    /// non-negative integer; some tools default to <c>0</c> (first page), others
    /// to <c>1</c>. Returns <c>null</c> when valid, otherwise a human-readable
    /// reason.
    /// </summary>
    internal static string? ValidatePage(int page)
    {
        if (page < 0)
            return $"page {page} out of range (must be >= 0)";
        if (page > MaxPage)
            return $"page {page} out of range (max {MaxPage})";
        return null;
    }

    /// <summary>
    /// Validate a topic / category id for the id-driven tools. Returns
    /// <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateId(int id, string name)
    {
        if (id <= 0)
            return $"{name} {id} is invalid (must be a positive integer)";
        return null;
    }

    /// <summary>
    /// Validate the optional <c>category_slug</c> for <c>forum_get_latest</c>. The
    /// slug becomes a URL path segment (<c>/c/{slug}/l/latest.json</c>), so beyond
    /// length + control-char checks we reject a leading <c>-</c> and any path
    /// separator so it cannot traverse or masquerade as another path component.
    /// A <c>null</c>/empty slug is accepted (the tool falls back to the site-wide
    /// latest feed). Returns <c>null</c> when valid, otherwise a reason.
    /// </summary>
    internal static string? ValidateCategorySlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug))
            return null;
        if (slug.Length > MaxSlugLength)
            return $"category_slug too long ({slug.Length} chars; max {MaxSlugLength})";
        if (ContainsControlOrNewline(slug))
            return "category_slug contains control characters";
        if (slug[0] == '-')
            return "category_slug must not start with '-'";
        if (slug.Contains('/') || slug.Contains('\\'))
            return "category_slug must not contain a path separator";
        return null;
    }

    /// <summary>
    /// Validate a topic <c>title</c> for <c>forum_create_topic</c>. Returns
    /// <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "title is required";
        if (title.Length > MaxTitleLength)
            return $"title too long ({title.Length} chars; max {MaxTitleLength})";
        if (ContainsControlOrNewline(title))
            return "title contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a post/topic markdown <c>body</c> for the create tools. A body is
    /// free-form markdown, so newlines and tabs are legitimate; we only reject a
    /// missing body, a NUL / other non-whitespace control character, and an
    /// over-long blob. Returns <c>null</c> when valid, otherwise a reason.
    /// </summary>
    internal static string? ValidateBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "body is required";
        if (body.Length > MaxBodyLength)
            return $"body too long ({body.Length} chars; max {MaxBodyLength})";
        if (ContainsDisallowedControl(body))
            return "body contains control characters";
        return null;
    }

    /// <summary>
    /// Validate the optional <c>tags</c> array for <c>forum_create_topic</c>.
    /// A <c>null</c>/empty array is accepted. Returns <c>null</c> when valid,
    /// otherwise a human-readable reason.
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
            if (string.IsNullOrWhiteSpace(t))
                return $"tag #{i + 1} is empty";
            if (t.Length > MaxTagLength)
                return $"tag #{i + 1} too long ({t.Length} chars; max {MaxTagLength})";
            if (ContainsControlOrNewline(t))
                return $"tag #{i + 1} contains control characters";
        }
        return null;
    }

    /// <summary>
    /// Validate the <c>filter</c> selector for <c>forum_get_notifications</c>.
    /// Only the two documented values are accepted; anything else is rejected
    /// before the request. Returns <c>null</c> when valid, otherwise a reason.
    /// </summary>
    internal static string? ValidateNotificationFilter(string? filter)
    {
        if (string.IsNullOrEmpty(filter))
            return null; // tool treats an absent filter as "all"
        if (filter is "all" or "unread")
            return null;
        return "filter must be 'all' or 'unread'";
    }

    /// <summary>True if the string contains any control character (includes
    /// newlines and tabs). Used where no control character is ever legitimate
    /// (single-line fields, URL path/query values).</summary>
    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }

    /// <summary>True if the string contains a control character other than the
    /// whitespace controls legitimate in a multi-line markdown body (tab, CR, LF).
    /// A NUL or other C0 control is always rejected.</summary>
    private static bool ContainsDisallowedControl(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c) && c is not '\t' and not '\r' and not '\n') return true;
        return false;
    }
}
