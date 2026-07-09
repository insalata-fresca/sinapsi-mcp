using Cervello.Watcher.Domain;

namespace Cervello.Watcher.Normalize;

/// <summary>One side of a pending pair (an audio or a transcript staged alone).</summary>
public sealed record StagedFile(string Basename, string Kind, string FileId, string Sha256, DriveChange Change);

/// <summary>
/// A recording ready for NORMALIZE. In the MIXED-cases model a recording may be:
/// <list type="bullet">
///   <item><b>both</b> — audio + transcript sharing a basename (the common case);</item>
///   <item><b>audio-only</b> — audio present, <see cref="Transcript"/> null (no Google <c>.txt</c>);</item>
///   <item><b>transcript-only</b> — transcript present, <see cref="Audio"/> null (no audio blob).</item>
/// </list>
/// At least one side is non-null (a recording with neither side is not constructible).
/// </summary>
public sealed record PairedRecording
{
    public PairedRecording(string basename, StagedFile? audio, StagedFile? transcript)
    {
        if (string.IsNullOrWhiteSpace(basename))
            throw new ArgumentException("PairedRecording.Basename must be non-empty", nameof(basename));
        if (audio is null && transcript is null)
            throw new ArgumentException(
                "PairedRecording requires at least one side (audio or transcript)", nameof(audio));
        Basename = basename;
        Audio = audio;
        Transcript = transcript;
    }

    public string Basename { get; }

    /// <summary>The audio side, or <see langword="null"/> for a transcript-only recording.</summary>
    public StagedFile? Audio { get; }

    /// <summary>The transcript side, or <see langword="null"/> for an audio-only recording.</summary>
    public StagedFile? Transcript { get; }

    /// <summary>True iff both sides are present (a complete pair).</summary>
    public bool HasAudio => Audio is not null;

    /// <summary>True iff a Google <c>.txt</c> transcript side is present.</summary>
    public bool HasTranscript => Transcript is not null;
}

/// <summary>
/// Pairs an audio and a transcript into one recording iff they share an identical
/// extension-stripped basename (recording-normalize). Arrival-order tolerant: a
/// lone singleton WAITS in a pending state (not an error); the second file
/// completes the pair regardless of which arrived first. Purely in-memory over the
/// staged set — deterministic and side-effect free.
///
/// <para><b>Singleton flush (MIXED cases).</b> A single-sided file is NOT dropped:
/// after a scan/cycle completes, <see cref="FlushSingletons"/> emits every UNPAIRED
/// held file as a single-sided <see cref="PairedRecording"/> (audio-only or
/// transcript-only), so audio-only and transcript-only recordings import too. The
/// flush is idempotent within a process (a basename that later pairs stops being a
/// singleton).</para>
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
    /// Add a staged file; if it completes a pair, return it, else null (pending — the
    /// singleton is held and later emitted by <see cref="FlushSingletons"/> if it never pairs).
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

    /// <summary>
    /// Emit every currently-UNPAIRED held file as a single-sided recording: a held audio with
    /// no transcript → an audio-only <see cref="PairedRecording"/>; a held transcript with no
    /// audio → a transcript-only one. A basename with BOTH sides is a complete pair (already
    /// returned by <see cref="Offer"/>) and is NOT flushed. Deterministic order (by basename)
    /// so a scan is byte-reproducible. Call this at the END of a backfill scan / poll cycle,
    /// AFTER every file has been offered, so a recording whose two files were scanned separately
    /// still pairs and only genuinely single-sided ones flush as singletons.
    /// </summary>
    public IReadOnlyList<PairedRecording> FlushSingletons()
    {
        var singletons = new List<PairedRecording>();
        foreach (var basename in Pending().OrderBy(b => b, StringComparer.Ordinal))
        {
            if (_audio.TryGetValue(basename, out var a))
                singletons.Add(new PairedRecording(basename, a, null));          // audio-only
            else if (_transcript.TryGetValue(basename, out var t))
                singletons.Add(new PairedRecording(basename, null, t));          // transcript-only
        }
        return singletons;
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
