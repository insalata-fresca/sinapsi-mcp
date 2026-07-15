namespace Sinapsi.DeliveryEvaluator;

/// <summary>How a file was changed in a diff.</summary>
public enum ChangeKind
{
    /// <summary>File added.</summary>
    Added,
    /// <summary>File modified in place.</summary>
    Modified,
    /// <summary>File deleted.</summary>
    Deleted,
    /// <summary>File renamed/moved.</summary>
    Renamed,
}

/// <summary>
/// One file's worth of a change — the TRUSTED, effect-bearing part of a diff. The evaluator
/// classifies over these (path + changed content), never over the PR title/body
/// (<see cref="UntrustedChangeMetadata"/>). Immutable value object.
/// </summary>
/// <param name="Path">The repo-relative path touched. May be empty when a change is described in
/// prose with no explicit path — an empty/unclassifiable path is a fail-safe escalation signal,
/// never a silent "safe".</param>
/// <param name="Kind">How the file was changed.</param>
/// <param name="AddedLines">Content lines added by this change (the "+" side).</param>
/// <param name="RemovedLines">Content lines removed by this change (the "-" side) — load-bearing
/// for detecting a REMOVED auth check / audit emission (<c>docs/65 §3.3</c> never-auto).</param>
public sealed record FileChange(
    string Path,
    ChangeKind Kind,
    IReadOnlyList<string> AddedLines,
    IReadOnlyList<string> RemovedLines)
{
    /// <summary>Convenience: an added-only file change with no removed lines.</summary>
    public static FileChange Added_(string path, params string[] addedLines) =>
        new(path, ChangeKind.Added, addedLines, Array.Empty<string>());

    /// <summary>Every changed content line (added + removed), for value-signature scanning.</summary>
    public IEnumerable<string> AllChangedLines => AddedLines.Concat(RemovedLines);
}
