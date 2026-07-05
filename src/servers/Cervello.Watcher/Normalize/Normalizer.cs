using System.Globalization;
using System.Text;
using Cervello.Watcher.Domain;

namespace Cervello.Watcher.Normalize;

/// <summary>
/// Assigns a recording its deterministic <c>id</c> + <c>recorded_at</c> (D5) and
/// builds the immutable <see cref="Recording"/>. Determinism is the contract:
/// the same basename + audio createdTime + audio_sha256 always yields the same id;
/// distinct basenames or dates yield distinct ids.
///
/// - <c>recorded_at = createdTime</c> (fallback modifiedTime), formatted
///   <c>yyyy-MM-ddTHH:mm</c>.
/// - <c>id = {recorded_at:yyyyMMdd}-{slug(basename)}</c>; the audio_sha256 in the
///   dedupe key guards same-day same-basename collisions downstream.
/// </summary>
public sealed class Normalizer
{
    public Recording Normalize(PairedRecording pair)
    {
        var audioChange = pair.Audio.Change;
        var when = audioChange.CreatedTime
            ?? audioChange.ModifiedTime
            ?? throw new InvalidOperationException(
                $"cannot derive recorded_at: audio '{pair.Basename}' has neither createdTime nor modifiedTime");

        var recordedAt = FormatRecordedAt(when);
        var id = $"{when.UtcDateTime:yyyyMMdd}-{Slug(pair.Basename)}";

        return new Recording(
            id: id,
            basename: pair.Basename,
            audioSha256: pair.Audio.Sha256,
            audioDriveId: pair.Audio.FileId,
            txtDriveId: pair.Transcript.FileId,
            recordedAt: recordedAt,
            state: PipelineState.Normalized);
    }

    /// <summary><c>yyyy-MM-ddTHH:mm</c> in UTC (deterministic, minute precision per D5).</summary>
    internal static string FormatRecordedAt(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Lower-case, ASCII-fold (drop diacritics), and hyphenate. Non-alphanumeric
    /// runs collapse to a single hyphen; leading/trailing hyphens are trimmed.
    /// Deterministic and culture-invariant.
    /// </summary>
    internal static string Slug(string input)
    {
        var folded = FoldToAscii(input).ToLowerInvariant();
        var sb = new StringBuilder(folded.Length);
        var lastHyphen = false;
        foreach (var ch in folded)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastHyphen = false;
            }
            else if (!lastHyphen && sb.Length > 0)
            {
                sb.Append('-');
                lastHyphen = true;
            }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "recording" : slug;
    }

    private static string FoldToAscii(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
