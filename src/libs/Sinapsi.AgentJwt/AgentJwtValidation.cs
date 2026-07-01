namespace Sinapsi.AgentJwt;

// Plain-ASCII comment banner so this file diffs as TEXT.
//
// Input + options validation for the JWT minter. Two seams:
//
//   * ValidateAgent(...) guards the one public-API input (the agent name) at the
//     MintAsync seam. The agent name is interpolated into a filesystem path
//     (<KeyDir>/<agent>.json), so a name with a path separator, a NUL, control
//     characters, or "." / ".." is rejected before it can escape KeyDir or be
//     handed to the filesystem. Returns a clear reason instead of an opaque
//     IOException.
//
//   * ValidateOptions(...) is a fail-closed check on the bound configuration:
//     required config (issuer, audience project id, key dir) must be present and
//     well-formed, and the issuer must be an absolute http(s) URL. It THROWS an
//     ArgumentException naming the offending option rather than letting a
//     footgun default flow into a token request.

/// <summary>
/// Validates the public-API input (agent name) and the bound
/// <see cref="AgentJwtOptions"/>. Fail-closed: malformed input is rejected with
/// a clear reason, and invalid config throws naming the offending option.
/// </summary>
public static class AgentJwtValidation
{
    /// <summary>Upper bound on an agent name. A JWK filename component; a
    /// generous cap that still refuses an unbounded blob.</summary>
    public const int MaxAgentLength = 128;

    /// <summary>Upper bound on the audience project id string.</summary>
    public const int MaxAudienceLength = 256;

    /// <summary>
    /// Validate the <c>agent</c> argument to <see cref="AgentJwtMinter.MintAsync"/>.
    /// Returns <c>null</c> when valid, otherwise a human-readable reason. The name
    /// becomes a filesystem path component, so path separators, traversal, NUL,
    /// and control characters are rejected.
    /// </summary>
    public static string? ValidateAgent(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
            return "agent is required";
        if (agent.Length > MaxAgentLength)
            return $"agent too long ({agent.Length} chars; max {MaxAgentLength})";
        foreach (var c in agent)
        {
            if (char.IsControl(c))
                return "agent contains control characters";
            if (c is '/' or '\\')
                return "agent must not contain a path separator";
        }
        if (agent is "." or "..")
            return "agent must not be '.' or '..'";
        return null;
    }

    /// <summary>
    /// Fail-closed validation of the bound options at construction time. Throws
    /// an <see cref="ArgumentException"/> naming the offending option / env var
    /// when a required value is missing or malformed, rather than letting a
    /// silent default flow into a token request.
    /// </summary>
    public static void ValidateOptions(AgentJwtOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);

        if (string.IsNullOrWhiteSpace(opt.KeyDir))
            throw new ArgumentException(
                "AgentJwtOptions.KeyDir (AGENT_KEY_DIR) is required and must be non-empty.",
                nameof(opt));

        if (string.IsNullOrWhiteSpace(opt.Issuer))
            throw new ArgumentException(
                "AgentJwtOptions.Issuer (OIDC_ISSUER) is required and must be non-empty.",
                nameof(opt));

        if (!Uri.TryCreate(opt.Issuer, UriKind.Absolute, out var issuerUri)
            || (issuerUri.Scheme != Uri.UriSchemeHttp && issuerUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(
                $"AgentJwtOptions.Issuer (OIDC_ISSUER) must be an absolute http(s) URL; got '{opt.Issuer}'.",
                nameof(opt));

        if (string.IsNullOrWhiteSpace(opt.AudienceProjectId))
            throw new ArgumentException(
                "AgentJwtOptions.AudienceProjectId (OIDC_AUDIENCE_PROJECT_ID) is required and must be non-empty.",
                nameof(opt));

        if (opt.AudienceProjectId.Length > MaxAudienceLength)
            throw new ArgumentException(
                $"AgentJwtOptions.AudienceProjectId (OIDC_AUDIENCE_PROJECT_ID) too long " +
                $"({opt.AudienceProjectId.Length} chars; max {MaxAudienceLength}).",
                nameof(opt));

        foreach (var c in opt.AudienceProjectId)
            if (char.IsControl(c))
                throw new ArgumentException(
                    "AgentJwtOptions.AudienceProjectId (OIDC_AUDIENCE_PROJECT_ID) contains control characters.",
                    nameof(opt));

        if (opt.TtlMinutes < AgentJwtOptions.MinTtlMinutes || opt.TtlMinutes > AgentJwtOptions.MaxTtlMinutes)
            throw new ArgumentException(
                $"AgentJwtOptions.TtlMinutes (JWT_TTL_MIN)={opt.TtlMinutes} is out of range " +
                $"({AgentJwtOptions.MinTtlMinutes}..{AgentJwtOptions.MaxTtlMinutes}).",
                nameof(opt));
    }
}
