using System.Text.RegularExpressions;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// The v1 <see cref="IParticipantHintSource"/> — derives an ordered participant hint from the recording
/// FILENAME (design: participant-hint assignment; a calendar / operator-note source is additive later
/// behind the same seam). The Watcher's recording id is <c>{yyyyMMdd}-{slug}</c>; this strips the date
/// prefix and yields the remaining hyphen-separated slug tokens, in order, as the participant hint.
///
/// <para>When a repo working tree is supplied it GROUNDS each token against a person dossier
/// (<c>map/people/&lt;slug&gt;.md</c>) so a mis-heard basename never becomes a hint; without one it
/// returns the raw tokens (the offline/test path). Either way it is a HINT only — attribution uses it
/// solely for an unambiguous 1:1 assignment and never fabricates a name. The default (empty repo root
/// + a filename with no participant tokens) yields NO hint, so the worst-case plain transcript still
/// works (voices → "Unknown speaker N").</para>
/// </summary>
public sealed class FilenameParticipantHintSource : IParticipantHintSource
{
    // id = {yyyyMMdd}-{slug}; strip the leading 8-digit date + hyphen to get the filename slug tail.
    private static readonly Regex DatePrefix = new(@"^\d{8}-", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string? _repoRoot;

    /// <param name="repoWorkingTree">
    /// Optional CT-local <c>ste/cervello</c> working tree (holds <c>map/people/</c>). When set, filename
    /// tokens are admitted only if a person dossier exists (grounding). When null, raw tokens are returned.
    /// </param>
    public FilenameParticipantHintSource(string? repoWorkingTree = null)
    {
        _repoRoot = string.IsNullOrWhiteSpace(repoWorkingTree) ? null : repoWorkingTree;
    }

    public Task<IReadOnlyList<string>> GetParticipantsAsync(string recordingId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));

        var tokens = FilenameTokens(recordingId);
        var admitted = _repoRoot is null ? tokens : tokens.Where(DossierExists);
        // Deterministic order preserved (as they appear in the filename), deduped.
        var result = new List<string>();
        foreach (var t in admitted)
            if (!result.Contains(t, StringComparer.Ordinal))
                result.Add(t);
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    private static IEnumerable<string> FilenameTokens(string recordingId)
    {
        var slug = DatePrefix.Replace(recordingId, "");
        if (slug.Length == 0)
            yield break;
        foreach (var token in slug.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return token.ToLowerInvariant();
    }

    private bool DossierExists(string slug) =>
        !string.IsNullOrWhiteSpace(slug)
        && File.Exists(Path.Combine(_repoRoot!, "map", "people", $"{slug}.md"));
}

/// <summary>
/// A dictionary-backed <see cref="IParticipantHintSource"/> for tests + a host that injects hints
/// directly (recording id → ordered participant slugs). Deterministic, offline.
/// </summary>
public sealed class InMemoryParticipantHintSource(IReadOnlyDictionary<string, IReadOnlyList<string>> byRecording)
    : IParticipantHintSource
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _byRecording =
        byRecording ?? throw new ArgumentNullException(nameof(byRecording));

    public Task<IReadOnlyList<string>> GetParticipantsAsync(string recordingId, CancellationToken ct = default) =>
        Task.FromResult(_byRecording.TryGetValue(recordingId, out var v) ? v : Array.Empty<string>());
}
