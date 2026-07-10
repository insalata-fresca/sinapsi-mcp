using System.Globalization;
using System.Text;

namespace Cervello.Enrichment.Domain;

/// <summary>
/// Slugify an operator-typed person name into a stable <c>map/people/&lt;slug&gt;.md</c> /
/// voiceprint <c>person_slug</c> key (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c>
/// §6.3 "Derive the slug from the new name (Marco → marco, with a collision/spelling guard); a rename
/// that yields no valid slug is skipped — no-fabrication floor").
///
/// <para><b>No-fabrication floor.</b> <see cref="TrySlugify"/> returns FALSE for any name that
/// slugifies to empty (whitespace, punctuation-only, a bare extension). The V5 poller SKIPS such a
/// rename rather than inventing a name — a biometric enrollment is never keyed on a fabricated slug.</para>
/// </summary>
public static class PersonSlug
{
    /// <summary>
    /// Derive a lowercase ASCII slug from <paramref name="displayName"/> (accents folded, spaces +
    /// punctuation → single <c>-</c>, trimmed). Returns false (and a null slug) when the result is
    /// empty — the caller must then SKIP, never enroll. Strips a trailing file extension first
    /// (a Drive file renamed to <c>Marco.m4a</c> → <c>marco</c>, not <c>marco-m4a</c>).
    /// </summary>
    public static bool TrySlugify(string? displayName, out string? slug)
    {
        slug = null;
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        var name = StripKnownAudioExtension(displayName.Trim());

        // Fold accents (é → e) via decomposition, dropping combining marks.
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasDash = false;
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue; // drop the combining accent
            if (ch is >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if ((ch is >= 'a' and <= 'z') || (ch is >= '0' and <= '9'))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-'); // collapse any run of separators/other chars into a single dash
                lastWasDash = true;
            }
        }

        var result = sb.ToString().Trim('-');
        if (result.Length == 0)
            return false; // no-fabrication floor: nothing usable → skip, never invent

        slug = result;
        return true;
    }

    private static readonly string[] KnownAudioExtensions = [".m4a", ".mp3", ".wav", ".aac", ".ogg", ".mp4", ".flac"];

    private static string StripKnownAudioExtension(string name)
    {
        foreach (var ext in KnownAudioExtensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        return name;
    }
}
