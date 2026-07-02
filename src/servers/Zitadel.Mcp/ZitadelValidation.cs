namespace Zitadel.Mcp;

/// <summary>
/// Input validation for the ZITADEL MCP tools. Every tool parameter that flows into a
/// ZITADEL REST path segment (an id, escaped via <c>Uri.EscapeDataString</c>) or into a
/// request body is checked here BEFORE any HTTP call is issued, so malformed input is
/// rejected with a clear, structured error instead of being handed to the upstream (which
/// would either 4xx opaquely or, for a control-char blob, produce a confusing failure).
///
/// <para>
/// Each method returns <c>null</c> when the value is acceptable, otherwise a human-readable
/// reason string. None of them throw.
/// </para>
///
/// <para>
/// Note on injection safety: ids are URL-escaped by <see cref="Api.ZitadelClient"/> before
/// they reach a path, so this validation is a correctness + fail-fast guard, not the primary
/// defence. We still reject control characters / newlines (which have no place in an id, a
/// name, or an ISO timestamp) and a leading <c>-</c> defensively so a hostile value can never
/// be mistaken for a flag if it is ever passed onward to a CLI/tool.
/// </para>
/// </summary>
internal static class ZitadelValidation
{
    /// <summary>Upper bound on an opaque ZITADEL id (user/project/app id). ZITADEL ids are
    /// short numeric snowflakes (~18 digits); 128 is a generous cap that still refuses an
    /// obviously-bogus blob.</summary>
    internal const int MaxIdLength = 128;

    /// <summary>Upper bound on a free-text name / username. ZITADEL caps a resource name at
    /// 200; 200 keeps parity with the upstream limit.</summary>
    internal const int MaxNameLength = 200;

    /// <summary>Upper bound on a free-text description.</summary>
    internal const int MaxDescriptionLength = 500;

    /// <summary>Upper bound on a single URI (redirect / post-logout redirect).</summary>
    internal const int MaxUriLength = 2_048;

    /// <summary>Maximum number of URIs accepted in one redirect/post-logout list.</summary>
    internal const int MaxUris = 100;

    /// <summary>Upper bound on an ISO-8601 expiration timestamp string.</summary>
    internal const int MaxExpirationLength = 64;

    /// <summary>Upper bound on an OIDC enum value (appType / authMethodType / …).</summary>
    internal const int MaxEnumLength = 64;

    /// <summary>Upper bound on an agent-file basename (the machine-key filename).</summary>
    internal const int MaxAgentFileLength = 128;

    /// <summary>Lower + upper bounds on a paging limit.</summary>
    internal const int MinLimit = 1;
    internal const int MaxLimit = 1_000;

    /// <summary>
    /// Validate a required id path-segment parameter (user id, project id, app id). Returns
    /// <c>null</c> when valid, otherwise a human-readable reason naming the parameter.
    /// </summary>
    internal static string? ValidateId(string paramName, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return $"{paramName} is required";
        if (id.Length > MaxIdLength)
            return $"{paramName} too long ({id.Length} chars; max {MaxIdLength})";
        if (ContainsControlOrNewline(id))
            return $"{paramName} contains control characters";
        // An id becomes a URL path segment; embedded slashes would silently reshape the path.
        if (id.Contains('/') || id.Contains('\\'))
            return $"{paramName} must not contain a path separator";
        return null;
    }

    /// <summary>
    /// Validate a required free-text name / username. Returns <c>null</c> when valid.
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
    /// Validate an optional free-text description. A null/empty value is allowed (the tool
    /// supplies a default). Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return null;
        if (description.Length > MaxDescriptionLength)
            return $"description too long ({description.Length} chars; max {MaxDescriptionLength})";
        if (ContainsControlOrNewline(description))
            return "description contains control characters";
        return null;
    }

    /// <summary>
    /// Validate an OIDC enum-ish string (appType / authMethodType / accessTokenType /
    /// responseTypes / grantTypes entries). Free-form to ZITADEL but must be a bounded,
    /// control-char-free token. A null value is allowed (the tool supplies a default or omits
    /// the field). Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateEnum(string paramName, string? value)
    {
        if (value is null)
            return null;
        if (value.Length == 0)
            return $"{paramName} must not be empty";
        if (value.Length > MaxEnumLength)
            return $"{paramName} too long ({value.Length} chars; max {MaxEnumLength})";
        if (ContainsControlOrNewline(value))
            return $"{paramName} contains control characters";
        if (value[0] == '-')
            return $"{paramName} must not start with '-'";
        return null;
    }

    /// <summary>
    /// Validate a required list of enum tokens (responseTypes / grantTypes). A null list is
    /// allowed (the tool substitutes a default). Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateEnumList(string paramName, string[]? values)
    {
        if (values is null)
            return null;
        for (var i = 0; i < values.Length; i++)
            if (ValidateEnum($"{paramName}[{i}]", values[i]) is { } err)
                return err;
        return null;
    }

    /// <summary>
    /// Validate a required, non-empty list of redirect/post-logout URIs. When
    /// <paramref name="required"/> is <c>true</c> a null/empty list is rejected; otherwise a
    /// null list is accepted (omit-to-leave-unchanged). Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateUris(string paramName, string[]? uris, bool required)
    {
        if (uris is null || uris.Length == 0)
            return required ? $"{paramName} is required" : null;
        if (uris.Length > MaxUris)
            return $"too many {paramName} ({uris.Length}; max {MaxUris})";
        for (var i = 0; i < uris.Length; i++)
        {
            var u = uris[i];
            if (string.IsNullOrWhiteSpace(u))
                return $"{paramName}[{i}] is empty";
            if (u.Length > MaxUriLength)
                return $"{paramName}[{i}] too long ({u.Length} chars; max {MaxUriLength})";
            if (ContainsControlOrNewline(u))
                return $"{paramName}[{i}] contains control characters";
        }
        return null;
    }

    /// <summary>
    /// Validate an ISO-8601 expiration timestamp. Must be a bounded, control-char-free string
    /// that parses as a real timestamp. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateExpiration(string? expiration)
    {
        if (string.IsNullOrWhiteSpace(expiration))
            return "expiration is required";
        if (expiration.Length > MaxExpirationLength)
            return $"expiration too long ({expiration.Length} chars; max {MaxExpirationLength})";
        if (ContainsControlOrNewline(expiration))
            return "expiration contains control characters";
        if (!DateTimeOffset.TryParse(
                expiration,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out _))
            return "expiration is not a valid ISO-8601 timestamp";
        return null;
    }

    /// <summary>
    /// Validate a paging limit. Returns <c>null</c> when in range.
    /// </summary>
    internal static string? ValidateLimit(int limit)
    {
        if (limit is < MinLimit or > MaxLimit)
            return $"limit {limit} out of range ({MinLimit}..{MaxLimit})";
        return null;
    }

    /// <summary>
    /// Validate an agent-file basename for <c>create_machine_key</c>. The value becomes a
    /// filename under <c>AGENT_KEY_DIR</c>, so it MUST be a bare basename — no directory
    /// separators, no <c>..</c>, and bounded. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateAgentFile(string? agentFile)
    {
        if (string.IsNullOrWhiteSpace(agentFile))
            return "agent_file is required";
        if (agentFile.Length > MaxAgentFileLength)
            return $"agent_file too long ({agentFile.Length} chars; max {MaxAgentFileLength})";
        if (ContainsControlOrNewline(agentFile))
            return "agent_file contains control characters";
        if (!IsSafeBasename(agentFile))
            return "agent_file must be a bare basename (no '/', '\\' or '..')";
        return null;
    }

    /// <summary>Guard against path traversal — the value must be a bare basename.</summary>
    internal static bool IsSafeBasename(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..")
        && name == Path.GetFileName(name);

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }
}
