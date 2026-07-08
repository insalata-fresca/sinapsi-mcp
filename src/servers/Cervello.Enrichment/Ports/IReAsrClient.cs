using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for SELECTIVE re-ASR of individual garbled spans (spec <c>text-correction</c> →
/// "Selective re-ASR for garbled spans only"; DESIGN §5.2 step 3). Re-transcription runs ONLY
/// over spans the base transcript marks low-confidence/garbled — never the whole transcript. A
/// re-ASR result is EVIDENCE for a <see cref="CorrectionKind.Garbled"/> diff, graded by the
/// decision policy like any other correction. Live adapter proxies CT126 with span offsets; a
/// fake stands in for tests (no live endpoint, no audio).
/// </summary>
public interface IReAsrClient
{
    /// <summary>
    /// Re-transcribe one garbled span. Returns the clarified text and a confidence, or
    /// <see cref="ReAsrResult.Unclear"/> when re-ASR does not clarify the span (which the stage
    /// treats as "no evidence" → the span is left as-is, never guessed).
    /// </summary>
    Task<ReAsrResult> ReAsrAsync(string recordingId, TextSpan span, CancellationToken ct = default);
}

/// <summary>The outcome of a selective re-ASR over one span.</summary>
public sealed record ReAsrResult
{
    private ReAsrResult(bool clarified, string? text, double confidence)
    {
        Clarified = clarified;
        Text = text;
        Confidence = confidence;
    }

    /// <summary>Whether re-ASR produced a confident clarification for the span.</summary>
    public bool Clarified { get; }

    /// <summary>The clarified text (present only when <see cref="Clarified"/>).</summary>
    public string? Text { get; }

    /// <summary>The re-ASR confidence (0 when unclear).</summary>
    public double Confidence { get; }

    public static ReAsrResult Clear(string text, double confidence)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("a clarified re-ASR result must carry text", nameof(text));
        if (confidence is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "confidence must be in [0,1]");
        return new(clarified: true, text, confidence);
    }

    /// <summary>Re-ASR did not clarify the span — no evidence produced (leave as-is, never guess).</summary>
    public static ReAsrResult Unclear { get; } = new(clarified: false, null, 0.0);
}
