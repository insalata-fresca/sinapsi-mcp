using System.Text.Json;

namespace Metabase.Mcp;

// -----------------------------------------------------------------------------
// Input validation for the Metabase MCP tools. Plain-ASCII banner so this file
// always diffs as TEXT, never binary.
// -----------------------------------------------------------------------------

/// <summary>
/// Input validation for the Metabase MCP tools. Every tool parameter that flows into a
/// Metabase REST path segment (a raw id, or a value escaped via <c>Uri.EscapeDataString</c>),
/// into an HTTP method, or into a request body is checked here BEFORE any HTTP call is issued,
/// so malformed input is rejected with a clear, structured error instead of being handed to
/// the upstream (which would either 4xx opaquely or, for a control-char blob, produce a
/// confusing failure).
///
/// <para>
/// Each method returns <c>null</c> when the value is acceptable, otherwise a human-readable
/// reason string. None of them throw.
/// </para>
///
/// <para>
/// Note on injection safety: most ids are numeric parameters (compile-time typed as
/// <c>int</c>/<c>long</c>), and the string ids that DO reach a path are URL-escaped by
/// <see cref="Api.MetabaseClient"/>, so this validation is a correctness + fail-fast guard,
/// not the primary defence. We still reject control characters / newlines (which have no place
/// in a name, an id, a method, or a JSON body) and, where a value can reach a CLI/URL segment,
/// a leading <c>-</c> defensively so a hostile value can never be mistaken for a flag.
/// </para>
/// </summary>
internal static class MetabaseValidation
{
    /// <summary>The four HTTP methods the generic <c>request</c> escape hatch accepts.</summary>
    internal static readonly string[] AllowedMethods = { "GET", "POST", "PUT", "DELETE" };

    /// <summary>Upper bound on an opaque string id (e.g. a collection id or the literal
    /// <c>root</c>). Metabase ids are short integers; 128 is a generous cap that still refuses
    /// an obviously-bogus blob.</summary>
    internal const int MaxIdLength = 128;

    /// <summary>Upper bound on a free-text name (card / collection / dashboard / database
    /// display name). Metabase enforces its own ceiling upstream; 255 is a generous cap that
    /// refuses an unbounded blob before the wire.</summary>
    internal const int MaxNameLength = 255;

    /// <summary>Upper bound on a free-text description.</summary>
    internal const int MaxDescriptionLength = 2_000;

    /// <summary>Upper bound on a database engine token (e.g. <c>postgres</c>, <c>mysql</c>).</summary>
    internal const int MaxEngineLength = 64;

    /// <summary>Upper bound on a display-type token (<c>table</c>, <c>bar</c>, <c>line</c>...).</summary>
    internal const int MaxDisplayLength = 64;

    /// <summary>Upper bound on a search query string.</summary>
    internal const int MaxQueryLength = 1_000;

    /// <summary>Upper bound on the comma-list of search models.</summary>
    internal const int MaxModelsLength = 256;

    /// <summary>Upper bound on a native-SQL query string. Large but bounded so a pathological
    /// paste cannot balloon a request body without limit.</summary>
    internal const int MaxSqlLength = 100_000;

    /// <summary>Upper bound on a generic <c>request</c> path.</summary>
    internal const int MaxPathLength = 2_048;

    /// <summary>Upper bound on a JSON body/patch string. Bounds the request body so a
    /// pathological paste cannot balloon memory before it is parsed.</summary>
    internal const int MaxJsonLength = 1_000_000;

    /// <summary>
    /// Validate the <c>method</c> parameter of the generic <c>request</c> tool. The value flows
    /// into <c>new HttpMethod(method.ToUpperInvariant())</c>; anything but the four allowed
    /// verbs is rejected before any HTTP call. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
            return "method is required (GET, POST, PUT, or DELETE)";
        var m = method.Trim().ToUpperInvariant();
        if (ContainsControlOrNewline(m))
            return "method contains control characters";
        if (Array.IndexOf(AllowedMethods, m) < 0)
            return $"method '{method}' is not allowed (use GET, POST, PUT, or DELETE)";
        return null;
    }

    /// <summary>
    /// Validate the <c>path</c> parameter of the generic <c>request</c> tool. It becomes the
    /// request URI; we require it non-empty, bounded, control-char-free, and — to keep the
    /// escape hatch inside the Metabase API — an <c>/api/</c> relative path (a full <c>http</c>
    /// URL is rejected so a caller cannot redirect the server-held API key at an arbitrary
    /// host). Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "path is required (e.g. /api/dashboard/2)";
        if (path.Length > MaxPathLength)
            return $"path too long ({path.Length} chars; max {MaxPathLength})";
        if (ContainsControlOrNewline(path))
            return "path contains control characters";
        // The MCP holds the Metabase API key; the escape hatch must stay on the configured
        // instance. An absolute URL would let a caller point the key at any host.
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "path must be a relative /api/... path, not an absolute URL";
        var trimmed = path.TrimStart('/');
        if (!trimmed.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("api", StringComparison.OrdinalIgnoreCase))
            return "path must target the Metabase API (start with /api/)";
        return null;
    }

    /// <summary>
    /// Validate a required opaque string id that reaches a URL path segment (e.g. the
    /// collection id, which may be the literal <c>root</c>). Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateStringId(string paramName, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return $"{paramName} is required";
        if (id.Length > MaxIdLength)
            return $"{paramName} too long ({id.Length} chars; max {MaxIdLength})";
        if (ContainsControlOrNewline(id))
            return $"{paramName} contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a required free-text name. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateName(string paramName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"{paramName} is required";
        if (name.Length > MaxNameLength)
            return $"{paramName} too long ({name.Length} chars; max {MaxNameLength})";
        if (ContainsControlOrNewline(name))
            return $"{paramName} contains control characters";
        return null;
    }

    /// <summary>
    /// Validate an optional free-text description. A null/empty value is allowed (the field is
    /// omitted). Newlines are permitted inside a description; other control characters are not.
    /// Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return null;
        if (description.Length > MaxDescriptionLength)
            return $"description too long ({description.Length} chars; max {MaxDescriptionLength})";
        if (ContainsControlAllowingNewline(description))
            return "description contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a required database engine token (e.g. <c>postgres</c>). Returns <c>null</c>
    /// when valid.
    /// </summary>
    internal static string? ValidateEngine(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
            return "engine is required (e.g. postgres)";
        if (engine.Length > MaxEngineLength)
            return $"engine too long ({engine.Length} chars; max {MaxEngineLength})";
        if (ContainsControlOrNewline(engine))
            return "engine contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a required display-type token (e.g. <c>table</c>). Returns <c>null</c> when
    /// valid.
    /// </summary>
    internal static string? ValidateDisplay(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
            return "display is required (e.g. table)";
        if (display.Length > MaxDisplayLength)
            return $"display too long ({display.Length} chars; max {MaxDisplayLength})";
        if (ContainsControlOrNewline(display))
            return "display contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a required native-SQL query string. Newlines are legitimate in SQL, so only
    /// non-newline control characters are rejected. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "sql is required";
        if (sql.Length > MaxSqlLength)
            return $"sql too long ({sql.Length} chars; max {MaxSqlLength})";
        if (ContainsControlAllowingNewline(sql))
            return "sql contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a required search query string. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateQuery(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return "q is required";
        if (q.Length > MaxQueryLength)
            return $"q too long ({q.Length} chars; max {MaxQueryLength})";
        if (ContainsControlOrNewline(q))
            return "q contains control characters";
        return null;
    }

    /// <summary>
    /// Validate the optional <c>models</c> comma-list for <c>search</c>. A null/empty value is
    /// allowed (no filter). Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateModels(string? models)
    {
        if (string.IsNullOrWhiteSpace(models))
            return null;
        if (models.Length > MaxModelsLength)
            return $"models too long ({models.Length} chars; max {MaxModelsLength})";
        if (ContainsControlOrNewline(models))
            return "models contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a REQUIRED JSON body/patch string: non-empty, bounded, and well-formed JSON.
    /// The tools hand this to <c>JsonDocument.Parse</c>; validating here turns a parse failure
    /// into a clear structured error before any HTTP call rather than an unhandled exception.
    /// Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateRequiredJson(string paramName, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return $"{paramName} is required (a JSON string)";
        return ValidateJsonShape(paramName, json);
    }

    /// <summary>
    /// Validate an OPTIONAL JSON body/patch string. A null/empty value is allowed; a present
    /// value must be bounded and well-formed JSON. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateOptionalJson(string paramName, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return ValidateJsonShape(paramName, json);
    }

    private static string? ValidateJsonShape(string paramName, string json)
    {
        if (json.Length > MaxJsonLength)
            return $"{paramName} too long ({json.Length} chars; max {MaxJsonLength})";
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return $"{paramName} is not valid JSON: {ex.Message}";
        }
        return null;
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }

    // Permits '\n' / '\r' / '\t' (legitimate inside SQL and free text) but rejects any other
    // control character (NUL, ESC, bell, etc.).
    private static bool ContainsControlAllowingNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') return true;
        return false;
    }
}
