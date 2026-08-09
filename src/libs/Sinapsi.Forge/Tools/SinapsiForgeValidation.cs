namespace Sinapsi.Forge.Tools;

/// <summary>
/// Input validation for the shared git-forge tool surface. Every tool parameter
/// that flows into a forge REST URL — an owner/repo/username/org that becomes a
/// path segment, a branch/tag/ref, a repo path, a search query, a numeric id, a
/// result limit — is checked here BEFORE any HTTP request is issued, so malformed
/// input is rejected with a clear, structured <c>{ ok:false, error }</c> instead of
/// being handed to the forge (which would 4xx opaquely) or, worse, being smuggled
/// into a URL path.
///
/// <para>
/// Note on safety: request paths are built with <c>Uri.EscapeDataString</c> in the
/// adapters, so this validation is a correctness + fail-fast guard, not the sole
/// injection defence. We still reject path separators and leading <c>-</c> in a
/// value destined for a single path segment (owner/repo/username/org) so a hostile
/// value can never traverse into a neighbouring path segment or be mistaken for a
/// flag. Every helper returns <c>null</c> when the value is acceptable, otherwise a
/// human-readable reason; none of them throw.
/// </para>
/// </summary>
public static class SinapsiForgeValidation
{
    /// <summary>Upper bound on a single path-segment identifier (owner / repo /
    /// username / org). Forges cap these well below this; 100 is a generous ceiling
    /// that still refuses an obviously-bogus blob.</summary>
    public const int MaxSegmentLength = 100;

    /// <summary>Upper bound on a git ref / branch / tag name.</summary>
    public const int MaxRefLength = 255;

    /// <summary>Upper bound on a repo-relative file path.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>Upper bound on a free-text search query.</summary>
    public const int MaxQueryLength = 256;

    /// <summary>Upper bound on a result limit accepted by list/search tools.</summary>
    public const int MaxLimit = 1000;

    /// <summary>
    /// Validate a single path-segment identifier — <c>owner</c>, <c>repo</c>,
    /// <c>username</c>, <c>org</c>. These become a single segment of a forge REST
    /// URL, so a <c>/</c> would traverse into a neighbouring segment and a leading
    /// <c>-</c> could be mistaken for a flag; both are rejected. Returns <c>null</c>
    /// when valid, otherwise a reason naming <paramref name="paramName"/>.
    /// </summary>
    public static string? ValidateSegment(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{paramName} is required";
        if (value.Length > MaxSegmentLength)
            return $"{paramName} too long ({value.Length} chars; max {MaxSegmentLength})";
        if (ContainsControlOrNewline(value))
            return $"{paramName} contains control characters";
        if (value[0] == '-')
            return $"{paramName} must not start with '-'";
        if (value.Contains('/') || value.Contains('\\'))
            return $"{paramName} must not contain a path separator";
        return null;
    }

    /// <summary>Validate the paired <c>owner</c> + <c>repo</c> the vast majority of
    /// tools take. Returns the first failure, or <c>null</c> when both are valid.</summary>
    public static string? ValidateOwnerRepo(string? owner, string? repo)
        => ValidateSegment(owner, "owner") ?? ValidateSegment(repo, "repo");

    /// <summary>
    /// Validate a git ref / branch / tag name that reaches a URL path segment.
    /// Refs may not start with <c>-</c>, contain control chars/newlines, or exceed
    /// the ref cap; a path separator is allowed (git refs are hierarchical, e.g.
    /// <c>refs/heads/x</c>). An empty value is treated per <paramref name="required"/>.
    /// </summary>
    public static string? ValidateRef(string? value, string paramName, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? $"{paramName} is required" : null;
        if (value.Length > MaxRefLength)
            return $"{paramName} too long ({value.Length} chars; max {MaxRefLength})";
        if (ContainsControlOrNewline(value))
            return $"{paramName} contains control characters";
        if (value[0] == '-')
            return $"{paramName} must not start with '-'";
        return null;
    }

    /// <summary>
    /// Validate a repo-relative file path. Path separators are allowed (a path IS
    /// hierarchical); control chars/newlines and a leading <c>-</c> are rejected, and
    /// a <c>..</c> traversal component is refused. An empty value is treated per
    /// <paramref name="required"/>.
    /// </summary>
    public static string? ValidatePath(string? value, string paramName = "path", bool required = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? $"{paramName} is required" : null;
        if (value.Length > MaxPathLength)
            return $"{paramName} too long ({value.Length} chars; max {MaxPathLength})";
        if (ContainsControlOrNewline(value))
            return $"{paramName} contains control characters";
        if (value[0] == '-')
            return $"{paramName} must not start with '-'";
        foreach (var seg in value.Split('/'))
            if (seg == "..")
                return $"{paramName} must not contain a '..' traversal segment";
        return null;
    }

    /// <summary>Validate a required free-text field (title / body / message / name /
    /// query). Non-empty, capped, and control-char/newline-rejected only for the
    /// single-line fields where <paramref name="allowNewlines"/> is false.</summary>
    public static string? ValidateText(string? value, string paramName, int maxLength, bool required = true, bool allowNewlines = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? $"{paramName} is required" : null;
        if (value.Length > maxLength)
            return $"{paramName} too long ({value.Length} chars; max {maxLength})";
        if (!allowNewlines && ContainsControlOrNewline(value))
            return $"{paramName} contains control characters";
        return null;
    }

    /// <summary>Validate a search query (required, capped; newlines allowed as
    /// forges accept them in query text).</summary>
    public static string? ValidateQuery(string? query, string paramName = "query")
        => ValidateText(query, paramName, MaxQueryLength, required: true, allowNewlines: true);

    /// <summary>Validate a result limit: must be a positive integer no larger than
    /// <see cref="MaxLimit"/>. Returns <c>null</c> when valid.</summary>
    public static string? ValidateLimit(int limit, string paramName = "limit")
    {
        if (limit <= 0)
            return $"{paramName} must be positive (got {limit})";
        if (limit > MaxLimit)
            return $"{paramName} too large ({limit}; max {MaxLimit})";
        return null;
    }

    /// <summary>Validate a positive numeric id (issue/PR number, comment id, release
    /// id, notification id, …). Returns <c>null</c> when valid.</summary>
    public static string? ValidatePositiveId(long id, string paramName)
        => id <= 0 ? $"{paramName} must be a positive id (got {id})" : null;

    /// <summary>
    /// Validate a closed-set enum-ish parameter that reaches a forge query string —
    /// e.g. traffic <c>per=day|week</c>, forks <c>sort=newest|oldest|stargazers</c>.
    /// The comparison is ordinal case-sensitive: the forge's own vocabulary is
    /// lowercase and silently accepting a differently-cased value would hide a caller
    /// bug. Returns <c>null</c> when valid, otherwise a reason listing the allowed set.
    /// </summary>
    public static string? ValidateChoice(string? value, string paramName, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{paramName} is required (one of: {string.Join(" | ", allowed)})";
        foreach (var a in allowed)
            if (string.Equals(value, a, StringComparison.Ordinal))
                return null;
        // Echo the offending value only when it is short + printable, so a hostile blob
        // cannot smuggle control characters into the message we hand back.
        var shown = value.Length <= 40 && !ContainsControlOrNewline(value) ? $" (got \"{value}\")" : "";
        return $"{paramName} must be one of: {string.Join(" | ", allowed)}{shown}";
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }
}
