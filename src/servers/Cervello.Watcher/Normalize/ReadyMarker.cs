namespace Cervello.Watcher.Normalize;

/// <summary>
/// The "ready for enrichment" signal (D8 / recording-normalize "Local
/// ready-for-enrichment marker"). It is a LOCAL marker only — a file in a CT-local
/// inbox dir keyed by recording id — plus the <c>watcher_recording.state =
/// normalized</c> row. It publishes NOTHING to shared NATS: this type references no
/// NATS client, and neither does any type in this binary (invariant 3). Idempotent:
/// re-marking the same recording is a no-op.
/// </summary>
public sealed class ReadyMarker
{
    private readonly string _inboxDir;

    public ReadyMarker(string inboxDir)
    {
        _inboxDir = inboxDir;
        Directory.CreateDirectory(_inboxDir);
    }

    /// <summary>Path of the local marker for a recording id (no I/O).</summary>
    public string MarkerPath(string recordingId) => Path.Combine(_inboxDir, recordingId + ".ready");

    /// <summary>Create the local ready marker (idempotent). Returns true if newly created.</summary>
    public bool Mark(string recordingId)
    {
        var path = MarkerPath(recordingId);
        if (File.Exists(path))
            return false;
        File.WriteAllText(path, "normalized\n");
        return true;
    }

    public bool IsMarked(string recordingId) => File.Exists(MarkerPath(recordingId));
}
