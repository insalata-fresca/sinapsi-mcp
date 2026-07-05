using Cervello.Watcher.Domain;

namespace Cervello.Watcher.Normalize;

/// <summary>One side of a pending pair (an audio or a transcript staged alone).</summary>
public sealed record StagedFile(string Basename, string Kind, string FileId, string Sha256, DriveChange Change);

/// <summary>A completed pair: audio + transcript sharing a basename.</summary>
public sealed record PairedRecording(string Basename, StagedFile Audio, StagedFile Transcript);

/// <summary>
/// Pairs an audio and a transcript into one recording iff they share an identical
/// extension-stripped basename (recording-normalize). Arrival-order tolerant: a
/// lone singleton WAITS in a pending state (not an error); the second file
/// completes the pair regardless of which arrived first. Purely in-memory over the
/// staged set — deterministic and side-effect free.
/// </summary>
public sealed class Pairer
{
    private readonly Dictionary<string, StagedFile> _audio = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StagedFile> _transcript = new(StringComparer.Ordinal);

    public static string BasenameOf(string name)
    {
        var b = Path.GetFileNameWithoutExtension(name);
        return b;
    }

    /// <summary>
    /// Add a staged file; if it completes a pair, return it, else null (pending).
    /// </summary>
    public PairedRecording? Offer(StagedFile file)
    {
        if (file.Kind == "audio")
            _audio[file.Basename] = file;
        else if (file.Kind == "transcript")
            _transcript[file.Basename] = file;
        else
            return null;

        if (_audio.TryGetValue(file.Basename, out var a) &&
            _transcript.TryGetValue(file.Basename, out var t))
            return new PairedRecording(file.Basename, a, t);
        return null;
    }

    /// <summary>Basenames currently pending (staged on one side only).</summary>
    public IReadOnlyCollection<string> Pending()
    {
        var pend = new HashSet<string>(_audio.Keys, StringComparer.Ordinal);
        pend.SymmetricExceptWith(_transcript.Keys);
        return pend;
    }

    /// <summary>True iff both sides of <paramref name="basename"/> are staged.</summary>
    public bool IsPaired(string basename) =>
        _audio.ContainsKey(basename) && _transcript.ContainsKey(basename);
}
