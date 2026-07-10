using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>In-memory <see cref="IRecordingAudioRefResolver"/> — offline slice / tests. No I/O.</summary>
public sealed class InMemoryRecordingAudioRefResolver : IRecordingAudioRefResolver
{
    private readonly Dictionary<string, RecordingAudioRef> _refs = new(StringComparer.Ordinal);

    public InMemoryRecordingAudioRefResolver Add(RecordingAudioRef audioRef)
    {
        ArgumentNullException.ThrowIfNull(audioRef);
        _refs[audioRef.RecordingId] = audioRef;
        return this;
    }

    public Task<RecordingAudioRef?> ResolveAsync(string recordingId, CancellationToken ct = default) =>
        Task.FromResult(_refs.TryGetValue(recordingId, out var r) ? r : null);
}
