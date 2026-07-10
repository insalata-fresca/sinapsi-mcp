using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>In-memory <see cref="IVoiceprintNamingCandidateStore"/> — offline slice / tests. No I/O.</summary>
public sealed class InMemoryVoiceprintNamingCandidateStore : IVoiceprintNamingCandidateStore
{
    private readonly Dictionary<string, VoiceprintNamingCandidate> _byDriveFileId = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> ReplaceUnresolvedAsync(
        IReadOnlyList<VoiceprintNamingCandidate> candidates, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var deleted = _byDriveFileId.Values
            .Where(c => !c.Resolved)
            .Select(c => c.DriveFileId)
            .ToList();
        foreach (var id in deleted)
            _byDriveFileId.Remove(id);

        foreach (var c in candidates)
            _byDriveFileId[c.DriveFileId] = c;

        return Task.FromResult<IReadOnlyList<string>>(deleted);
    }

    public Task<VoiceprintNamingCandidate?> GetByDriveFileIdAsync(string driveFileId, CancellationToken ct = default) =>
        Task.FromResult(_byDriveFileId.TryGetValue(driveFileId, out var c) ? c : null);

    public Task<IReadOnlyList<VoiceprintNamingCandidate>> GetUnresolvedAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VoiceprintNamingCandidate>>(
            _byDriveFileId.Values.Where(c => !c.Resolved)
                .OrderBy(c => c.SampleName, StringComparer.Ordinal).ToList());

    public Task<bool> MarkResolvedAsync(string driveFileId, CancellationToken ct = default)
    {
        if (!_byDriveFileId.TryGetValue(driveFileId, out var c) || c.Resolved)
            return Task.FromResult(false);
        _byDriveFileId[driveFileId] = c with { Resolved = true };
        return Task.FromResult(true);
    }
}
