namespace Cervello.Enrichment.Domain;

/// <summary>
/// The REDACTED wire shape returned by <c>cervello_open_points_list</c> (spec
/// <c>open-points-mcp</c> → "List pending open-points with decision context"). It is the entry the
/// operator sees in Claude web/mobile: refs + a one-line redacted question + scored candidates, and
/// NOTHING ELSE — no transcript body, audio, embedding vector, or mail body (lint R10). The engine
/// projects an <see cref="OpenPoint"/> to this view; the projection is the redaction boundary.
/// </summary>
public sealed record OpenPointView
{
    public OpenPointView(
        string pointId,
        OpenPointKind kind,
        string recording,
        string bundle,
        string question,
        IReadOnlyList<ScoredCandidateView> candidates)
    {
        PointId = pointId;
        Kind = kind;
        Recording = recording;
        Bundle = bundle;
        Question = question;
        Candidates = candidates;
    }

    /// <summary>The open-point id (e.g. <c>op_…</c>).</summary>
    public string PointId { get; }

    /// <summary>The point kind as a lowercase wire token (<c>speaker | correction | link | timeline</c>).</summary>
    public string KindWire => Kind switch
    {
        OpenPointKind.Speaker => "speaker",
        OpenPointKind.Correction => "correction",
        OpenPointKind.Fact => "link",
        _ => "link",
    };

    public OpenPointKind Kind { get; }

    /// <summary>The recording ref (<c>rec://&lt;id&gt;</c>).</summary>
    public string Recording { get; }

    /// <summary>The bundle back-link (<c>bundle://&lt;id&gt;</c>).</summary>
    public string Bundle { get; }

    /// <summary>The one-line redacted question.</summary>
    public string Question { get; }

    /// <summary>The scored candidate answers.</summary>
    public IReadOnlyList<ScoredCandidateView> Candidates { get; }

    /// <summary>Project a stored <see cref="OpenPoint"/> to the redacted list-view entry.</summary>
    public static OpenPointView From(OpenPoint p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new OpenPointView(
            pointId: p.PointId,
            kind: p.Kind,
            recording: p.RecordingId.StartsWith("rec://", StringComparison.Ordinal) ? p.RecordingId : $"rec://{p.RecordingId}",
            bundle: p.BundleRef,
            question: p.QuestionRedacted,
            candidates: p.Candidates.Select(c => new ScoredCandidateView(c.Value, c.Confidence, c.Why)).ToList());
    }
}

/// <summary>A scored candidate as it appears in an <see cref="OpenPointView"/> (redacted).</summary>
public sealed record ScoredCandidateView(string Value, double Confidence, string Why);
