using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// The v1 <see cref="IPriorSource"/> — derives the candidate set from the recording FILENAME +
/// manifest PARTICIPANTS (discovery Q6: "v1 can ship with filename+participants priors and add a
/// structured org-chart source additive later"). This is the offline, deterministic prior: it is
/// injected the recording→candidates mapping (which the watcher/manifest supplies), and reports a
/// STRONG prior when the filename explicitly names a person (e.g. <c>Guilhem 121…</c>). It never
/// resolves an identity — it only narrows the candidate set the decision policy weighs.
///
/// <para>A structured org-chart prior is an additive future source implementing the same seam; the
/// resolver stays agnostic to which prior produced the candidates.</para>
/// </summary>
public sealed class FilenameParticipantPriorSource(
    IReadOnlyDictionary<string, PriorCandidates> priorsByRecording) : IPriorSource
{
    private readonly IReadOnlyDictionary<string, PriorCandidates> _priors =
        priorsByRecording ?? throw new ArgumentNullException(nameof(priorsByRecording));

    public Task<PriorCandidates> GetPriorAsync(string recordingId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        return Task.FromResult(_priors.GetValueOrDefault(recordingId, PriorCandidates.None));
    }
}
