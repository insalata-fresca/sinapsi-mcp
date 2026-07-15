namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// The result of a shadow-vs-would-enforce stream diff, with the AUTO-FAIL promotion gate.
///
/// <para>The gate <see cref="PromotionAutoFailed"/> is true when there is ANY deviation OR ANY
/// unverifiable record: a deterministic evaluator must reproduce every shadow decision exactly, and
/// a record that cannot be recomputed cannot be certified (fail-safe). <see cref="UnsafeDeviations"/>
/// isolates the critical subset — cases where enforcement would ALLOW what shadow held.</para>
/// </summary>
public sealed record StreamDiffReport(IReadOnlyList<StreamDiffRow> Rows)
{
    /// <summary>Total decisions diffed.</summary>
    public int Total => Rows.Count;

    /// <summary>Rows where would-enforce reproduced the shadow verdict.</summary>
    public int Matches => Rows.Count(r => r.Status == DiffStatus.Match);

    /// <summary>Rows where would-enforce differed from the shadow verdict.</summary>
    public IReadOnlyList<StreamDiffRow> Deviations =>
        Rows.Where(r => r.Status == DiffStatus.Deviation).ToList();

    /// <summary>The critical subset: would-enforce ALLOWS what shadow held (a permissive regression).</summary>
    public IReadOnlyList<StreamDiffRow> UnsafeDeviations =>
        Rows.Where(r => r.Status == DiffStatus.Deviation && r.Direction == DeviationDirection.MorePermissive).ToList();

    /// <summary>Records that carried no change and could not be recomputed (fail-safe: block promotion).</summary>
    public IReadOnlyList<StreamDiffRow> Unverifiable =>
        Rows.Where(r => r.Status == DiffStatus.Unverifiable).ToList();

    /// <summary>THE gate. True when promotion must be auto-failed: any deviation, or any
    /// unverifiable record.</summary>
    public bool PromotionAutoFailed => Deviations.Count > 0 || Unverifiable.Count > 0;

    /// <summary>Plain gate word for a human summary.</summary>
    public string GateWord => PromotionAutoFailed ? "AUTO-FAILED" : "PASSED";

    /// <summary>A one-line human summary.</summary>
    public string Summary =>
        $"stream-diff: {Total} decisions, {Matches} match, {Deviations.Count} deviation(s) " +
        $"({UnsafeDeviations.Count} unsafe), {Unverifiable.Count} unverifiable → promotion {GateWord}";
}
