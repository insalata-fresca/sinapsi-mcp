namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// A change under review: its TRUSTED effect (<see cref="Files"/>) and its UNTRUSTED declared
/// intent (<see cref="Metadata"/>), plus an optional <see cref="CorrelationId"/> that threads the
/// verdict to the request across the Q1/Q2/Q3 layers (home-server <c>docs/61 §8</c>).
///
/// <para>The split is the whole point: <see cref="DeterministicRiskClassifier"/> reads only
/// <see cref="Files"/>. <see cref="Metadata"/> is present so it can be logged, never so it can move
/// the verdict (<c>docs/65</c> principle 2 — the diff/PR body is untrusted input).</para>
/// </summary>
/// <param name="Files">The trusted, effect-bearing file changes.</param>
/// <param name="Metadata">The untrusted declared intent (PR title/body/labels).</param>
/// <param name="CorrelationId">Optional trace id joining this decision to the request.</param>
public sealed record ChangeSet(
    IReadOnlyList<FileChange> Files,
    UntrustedChangeMetadata Metadata,
    string CorrelationId = "")
{
    /// <summary>Build a change set from files with no declared intent.</summary>
    public static ChangeSet Of(params FileChange[] files) =>
        new(files, UntrustedChangeMetadata.None);

    /// <summary>Build a change set from files with untrusted metadata attached.</summary>
    public static ChangeSet Of(IReadOnlyList<FileChange> files, UntrustedChangeMetadata metadata, string correlationId = "") =>
        new(files, metadata, correlationId);

    /// <summary>
    /// True when the change cannot be parsed into anything an effect classifier can reason about —
    /// no files, or every file has an empty/whitespace path AND no changed content. Per
    /// <c>docs/65</c> principle 3 / <c>docs/61 §8</c> this is a fail-safe escalation +
    /// dead-letter case, never a silent allow.
    /// </summary>
    public bool IsUnparseable =>
        Files is null || Files.Count == 0 ||
        Files.All(f => string.IsNullOrWhiteSpace(f.Path) && !f.AllChangedLines.Any());
}
