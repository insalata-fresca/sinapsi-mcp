namespace Cervello.Enrichment.Domain;

/// <summary>
/// The SCHEMAS §1 source-ref grammar — <c>&lt;scheme&gt;://&lt;id&gt;[#&lt;fragment&gt;]</c> or a
/// repo-relative path — as a small, shared validator. The pack contract (design §2.1) and the
/// evidence-linkage rule (design §3.2 / LINT R1) both require that every asserted item cites a
/// <b>resolvable</b> ref; this centralises "is this a registered scheme?" so the pack assembler, the
/// evidence route, and pin-on-cite all agree on the grammar (matching
/// <see cref="Adapters.CervelloGraphWriter"/>'s external-ref rule).
///
/// <para>Registered schemes (SCHEMAS §1): <c>pin://</c>, <c>rec://</c>, <c>drive://</c>,
/// <c>gmail://</c>, <c>bundle://</c>. Anything else with no <c>://</c> is treated as a repo-relative
/// path (the SCHEMAS §1 "(path)" row) and accepted — existence is a resolver concern, not a grammar
/// one. This validator checks GRAMMAR only; the on-CT resolver checks existence.</para>
/// </summary>
public static class SourceRef
{
    /// <summary>The registered non-path schemes (SCHEMAS §1).</summary>
    public static readonly string[] Schemes = ["pin", "rec", "drive", "gmail", "bundle"];

    /// <summary>The external, custody-tiered schemes that must be PINNED on cite in a merged map line (LINT R11).</summary>
    public static readonly string[] ExternalSchemes = ["drive", "gmail"];

    /// <summary>
    /// True iff <paramref name="reference"/> is a grammatically valid source ref: a registered
    /// scheme with a non-empty id, OR a repo-relative path (no <c>://</c>, non-empty, not absolute).
    /// Empty/whitespace → false. Existence is NOT checked here.
    /// </summary>
    public static bool IsResolvableScheme(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var idx = reference.IndexOf("://", StringComparison.Ordinal);
        if (idx < 0)
            // Repo-relative path row: a non-empty, non-absolute path token (may carry a #heading).
            return !reference.StartsWith('/') && !reference.Contains(' ');
        var scheme = reference[..idx];
        var rest = reference[(idx + 3)..];
        if (rest.Length == 0) return false;              // scheme:// with no id is not resolvable
        return Array.Exists(Schemes, s => s.Equals(scheme, StringComparison.Ordinal));
    }

    /// <summary>True iff the ref is an external (drive://|gmail://) scheme that must be pinned on cite (R11).</summary>
    public static bool IsExternal(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var idx = reference.IndexOf("://", StringComparison.Ordinal);
        if (idx < 0) return false;
        var scheme = reference[..idx];
        return Array.Exists(ExternalSchemes, s => s.Equals(scheme, StringComparison.Ordinal));
    }
}
