namespace Cervello.Enrichment.Domain;

/// <summary>
/// The §10 enrollment allowlist (DESIGN §10.1; voiceprint-store spec → "Enroll / refine on
/// confirmation, under the allowlist"). Voice enrollment is a hard-gated biometric write:
/// permitted ONLY for the operator himself, people who explicitly agreed, and speakers in
/// operator-attended recordings made with participant awareness. Enrollment MUST NOT occur for
/// anyone outside this set — this is a build-gating control, not advisory.
///
/// <para>This value object is the machine-checkable form of that allowlist: the enroll/refine
/// path consults <see cref="IsAllowed"/> before any centroid write and refuses otherwise. The
/// allowlist is data (who consented) held CT-side, injected — never inferred by the engine.</para>
/// </summary>
public sealed record EnrollmentAllowlist
{
    private readonly HashSet<string> _allowed;

    public EnrollmentAllowlist(IEnumerable<string> allowedPersonSlugs)
    {
        ArgumentNullException.ThrowIfNull(allowedPersonSlugs);
        _allowed = new HashSet<string>(
            allowedPersonSlugs.Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.Ordinal);
    }

    /// <summary>An empty allowlist — nobody may be enrolled (safe default until consent is recorded).</summary>
    public static EnrollmentAllowlist Empty { get; } = new(Array.Empty<string>());

    /// <summary>Whether <paramref name="personSlug"/> is on the §10 enrollment allowlist.</summary>
    public bool IsAllowed(string personSlug) =>
        !string.IsNullOrWhiteSpace(personSlug) && _allowed.Contains(personSlug);

    /// <summary>The allowlisted slugs (for logging / audit).</summary>
    public IReadOnlyCollection<string> AllowedSlugs => _allowed;
}

/// <summary>
/// Thrown when an enroll/refine is attempted for a person NOT on the §10 allowlist. The
/// enrollment is refused (not silently skipped) so the caller cannot mistake a blocked write
/// for a success.
/// </summary>
public sealed class EnrollmentNotAllowedException(string personSlug)
    : Exception($"enrollment refused: '{personSlug}' is not on the §10 enrollment allowlist")
{
    public string PersonSlug { get; } = personSlug;
}
