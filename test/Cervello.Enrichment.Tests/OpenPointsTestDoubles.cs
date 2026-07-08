using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>Fake access log: collects the redacted entries so a test can assert scope + logging.</summary>
internal sealed class FakeAccessLog : IAccessLog
{
    public List<AccessLogEntry> Entries { get; } = [];

    public Task AppendAsync(AccessLogEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fake enrollment-source provider: returns a pre-seeded confirmed centroid for a (recording,
/// speaker) key, or null. Lets a speaker-answer test drive the enroll path without any real audio.
/// </summary>
internal sealed class FakeEnrollmentSourceProvider : IEnrollmentSourceProvider
{
    private readonly Dictionary<string, EnrollmentSource> _sources = new(StringComparer.Ordinal);

    public void Seed(string recordingId, string mergedSpeaker, EnrollmentSource source) =>
        _sources[Key(recordingId, mergedSpeaker)] = source;

    public Task<EnrollmentSource?> GetConfirmedSourceAsync(string recordingId, string mergedSpeaker, CancellationToken ct = default)
    {
        _sources.TryGetValue(Key(recordingId, mergedSpeaker), out var s);
        return Task.FromResult(s);
    }

    private static string Key(string rec, string spk) => $"{rec}#{spk}";
}
