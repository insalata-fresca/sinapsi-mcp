namespace Cervello.Enrichment.Domain;

/// <summary>The kind of fact an open-point gates (DESIGN §6.1).</summary>
public enum OpenPointKind
{
    /// <summary>A speaker attribution the policy escalated (ambiguous / conflicting).</summary>
    Speaker,

    /// <summary>A text correction the policy escalated (two plausible resolutions).</summary>
    Correction,

    /// <summary>A proposed link/timeline fact the policy escalated.</summary>
    Fact,
}

/// <summary>
/// A durable open-point (DESIGN §6.1): a question the engine cannot answer with sufficient
/// evidence, awaiting the operator via the cervello MCP. Back-links its bundle + the specific
/// proposed fact it gates. Carries NO mail bodies / audio / biometrics — only a REDACTED question
/// + SCORED candidate values (lint R10 redaction posture). Resolved only by an operator answer,
/// which sets a <c>human://&lt;answer-id&gt;</c> basis.
/// </summary>
public sealed record OpenPoint
{
    /// <summary>Primary ctor: scored candidates (spec <c>open-points-mcp</c> list shape).</summary>
    public OpenPoint(
        string pointId,
        OpenPointKind kind,
        string recordingId,
        string bundleId,
        string questionRedacted,
        IReadOnlyList<ScoredCandidate> candidates,
        string? mergedSpeaker = null)
    {
        if (string.IsNullOrWhiteSpace(pointId))
            throw new ArgumentException("OpenPoint.PointId must be non-empty", nameof(pointId));
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("OpenPoint.RecordingId must be non-empty", nameof(recordingId));
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new ArgumentException("OpenPoint.BundleId must be non-empty", nameof(bundleId));
        if (string.IsNullOrWhiteSpace(questionRedacted))
            throw new ArgumentException("OpenPoint.QuestionRedacted must be non-empty", nameof(questionRedacted));
        ArgumentNullException.ThrowIfNull(candidates);
        PointId = pointId;
        Kind = kind;
        RecordingId = recordingId;
        BundleId = bundleId;
        QuestionRedacted = questionRedacted;
        Candidates = candidates;
        MergedSpeaker = mergedSpeaker;
    }

    /// <summary>
    /// Back-compat ctor for callers producing a bare candidate value list (e.g. the apply stage's
    /// escalated verdicts). Each string becomes an <see cref="ScoredCandidate.Unscored"/> value.
    /// </summary>
    public OpenPoint(
        string pointId,
        OpenPointKind kind,
        string recordingId,
        string bundleId,
        string questionRedacted,
        IReadOnlyList<string> candidateValues,
        string? mergedSpeaker = null)
        : this(pointId, kind, recordingId, bundleId, questionRedacted,
            (candidateValues ?? throw new ArgumentNullException(nameof(candidateValues)))
                .Select(ScoredCandidate.Unscored).ToList(),
            mergedSpeaker)
    {
    }

    public string PointId { get; }
    public OpenPointKind Kind { get; }
    public string RecordingId { get; }
    public string BundleId { get; }

    /// <summary>The redacted question (no bodies/snippets; refs + candidates only — R10).</summary>
    public string QuestionRedacted { get; }

    public IReadOnlyList<ScoredCandidate> Candidates { get; }

    /// <summary>
    /// The merged speaker cluster label this point gates (for a <see cref="OpenPointKind.Speaker"/>
    /// point) — used to build the <c>rec://&lt;id&gt;#&lt;seg&gt;</c> source ref on answer. Null for
    /// non-speaker points.
    /// </summary>
    public string? MergedSpeaker { get; }

    /// <summary>The bundle back-link (R5) this point gates.</summary>
    public string BundleRef => $"bundle://{BundleId}";
}
