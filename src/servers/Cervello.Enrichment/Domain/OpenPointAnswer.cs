namespace Cervello.Enrichment.Domain;

/// <summary>How the operator answers an open-point (spec <c>open-points-mcp</c> →
/// "Answer an open-point"). Exactly one of three shapes.</summary>
public enum AnswerMode
{
    /// <summary>Select one of the point's scored candidates by value.</summary>
    Select,

    /// <summary>Supply a free value the candidates did not offer.</summary>
    Value,

    /// <summary>Dismiss the point — omit the fact, never guessed; record the dismissal.</summary>
    Dismiss,
}

/// <summary>
/// An operator answer to an open-point. A resolving answer (<see cref="AnswerMode.Select"/> /
/// <see cref="AnswerMode.Value"/>) supplies the confirmed value; a <see cref="AnswerMode.Dismiss"/>
/// carries none. On resolution the engine writes the fact carrying <c>basis: human://&lt;answer-id&gt;</c>.
/// </summary>
public sealed record OpenPointAnswer
{
    private OpenPointAnswer(AnswerMode mode, string? value)
    {
        Mode = mode;
        Value = value;
    }

    public AnswerMode Mode { get; }

    /// <summary>The confirmed value for a select/value answer; null for a dismiss.</summary>
    public string? Value { get; }

    /// <summary>Whether this answer resolves the point with a confirmed fact (vs. dismissing it).</summary>
    public bool IsResolving => Mode is AnswerMode.Select or AnswerMode.Value;

    /// <summary>Select one of the point's candidates by value.</summary>
    public static OpenPointAnswer Select(string candidateValue)
    {
        if (string.IsNullOrWhiteSpace(candidateValue))
            throw new ArgumentException("select answer must name a candidate value", nameof(candidateValue));
        return new OpenPointAnswer(AnswerMode.Select, candidateValue);
    }

    /// <summary>Supply a free value not among the offered candidates.</summary>
    public static OpenPointAnswer Value_(string freeValue)
    {
        if (string.IsNullOrWhiteSpace(freeValue))
            throw new ArgumentException("value answer must supply a non-empty value", nameof(freeValue));
        return new OpenPointAnswer(AnswerMode.Value, freeValue);
    }

    /// <summary>Dismiss the point — the fact is omitted (never guessed) and the dismissal recorded.</summary>
    public static OpenPointAnswer Dismiss() => new(AnswerMode.Dismiss, null);
}
