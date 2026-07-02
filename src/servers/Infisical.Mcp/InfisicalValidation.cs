namespace Infisical.Mcp;

/// <summary>
/// Input validation for the Infisical tool surface. Every tool parameter that flows
/// into an Infisical REST call — a folder name, a secret-path segment, a secret key —
/// is checked here BEFORE any HTTP request is made, so malformed input is rejected with
/// a clear, structured error instead of being handed to the API (which would either
/// fail opaquely or craft a surprising path).
///
/// <para>
/// The <c>group</c> and <c>service</c> values are interpolated into secret PATHS
/// (<c>/&lt;group&gt;/&lt;service&gt;/&lt;name&gt;</c>) and folder names; <c>name</c>
/// becomes a URL path segment (<c>/v3/secrets/raw/&lt;name&gt;</c>). A stray slash,
/// control character, or leading <c>-</c> in any of these could produce a path that
/// escapes the intended folder or is mistaken for a flag downstream, so each is rejected
/// defensively. This is a correctness + fail-fast guard: values reach the API via typed
/// JSON bodies / URL-escaped query parameters, not a shell, so this is not the primary
/// injection defence — but a hostile value must never be able to redirect where a secret
/// is written or read.
/// </para>
///
/// <para>Every method returns <c>null</c> when the value is valid, otherwise a
/// human-readable reason. None of them throw.</para>
/// </summary>
internal static class InfisicalValidation
{
    /// <summary>Upper bound on a path segment (group / service). Infisical folder names
    /// are short slugs; 128 is a generous cap that still refuses an unbounded blob.</summary>
    internal const int MaxSegmentLength = 128;

    /// <summary>Upper bound on a secret key name. Env-var-style keys are short; 256 is a
    /// generous ceiling.</summary>
    internal const int MaxNameLength = 256;

    /// <summary>Upper bound on a caller-supplied secret value (<c>set_secret</c>). A
    /// vendor token or PEM blob is well under this; 65536 (64 KiB) refuses a pathological
    /// paste without a large-object-heap allocation.</summary>
    internal const int MaxValueLength = 65_536;

    /// <summary>Upper bound on the requested random-secret byte count. 4096 bytes → an
    /// 8192-char hex string, far past any real secret; a larger request is a footgun,
    /// not a legitimate need.</summary>
    internal const int MaxRandomBytes = 4_096;

    /// <summary>Validate a path segment (<c>group</c> or <c>service</c>). The value is
    /// interpolated into a secret path and used as a folder name, so it must be a plain,
    /// single-segment slug: non-empty, bounded, no control characters, no path separators,
    /// and not a leading <c>-</c>.</summary>
    internal static string? ValidateSegment(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{paramName} is required";
        if (value.Length > MaxSegmentLength)
            return $"{paramName} too long ({value.Length} chars; max {MaxSegmentLength})";
        if (ContainsControlOrNewline(value))
            return $"{paramName} contains control characters";
        if (value.Contains('/') || value.Contains('\\'))
            return $"{paramName} must not contain a path separator";
        if (value[0] == '-')
            return $"{paramName} must not start with '-'";
        return null;
    }

    /// <summary>Validate a secret key <c>name</c>. It becomes a URL path segment on the
    /// secrets endpoint, so the same single-segment rules apply, with a larger length cap.</summary>
    internal static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "name is required";
        if (name.Length > MaxNameLength)
            return $"name too long ({name.Length} chars; max {MaxNameLength})";
        if (ContainsControlOrNewline(name))
            return "name contains control characters";
        if (name.Contains('/') || name.Contains('\\'))
            return "name must not contain a path separator";
        if (name[0] == '-')
            return "name must not start with '-'";
        return null;
    }

    /// <summary>Validate a caller-supplied secret <c>value</c> (<c>set_secret</c>). The
    /// value is free-form (it may legitimately contain any byte a vendor token carries),
    /// so we only reject the two abuse cases: an empty value (nothing to store) and an
    /// over-long blob.</summary>
    internal static string? ValidateValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "value is required";
        if (value.Length > MaxValueLength)
            return $"value too long ({value.Length} chars; max {MaxValueLength})";
        return null;
    }

    /// <summary>Validate the requested random-secret byte count. A non-positive count is
    /// tolerated at the call site (it defaults to 32), so it is NOT rejected here — only an
    /// absurdly large request is refused so a caller cannot force a huge allocation.</summary>
    internal static string? ValidateRandomBytes(int bytes)
    {
        if (bytes > MaxRandomBytes)
            return $"bytes {bytes} out of range (max {MaxRandomBytes})";
        return null;
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }
}
