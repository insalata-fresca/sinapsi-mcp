namespace Sinapsi.Governance.Events;

/// <summary>
/// One governance FACT: what happened, on which subject, with a human-legible summary.
/// Immutable. The <see cref="Subject"/> is always a <see cref="GovernanceChannels"/>
/// fact subject (constructed via the factory helpers, so the fact-not-trigger discipline
/// holds by construction).
/// </summary>
public sealed record GovernanceEvent(string Subject, string Kind, string Summary, DateTimeOffset At)
{
    /// <summary>A trust-ledger change fact.</summary>
    public static GovernanceEvent Trust(TrustLedgerEntry entry, ShadowOutcome? outcome, string? note)
    {
        var subject = GovernanceChannels.Trust(entry.ChangeClass, entry.Authority);
        var outcomeText = outcome is { } o ? $" outcome={o}" : "";
        var noteText = string.IsNullOrEmpty(note) ? "" : $" ({note})";
        var summary =
            $"trust[{entry.ChangeClass}] score={entry.Score:0.00} streak={entry.ConsecutiveReliable} " +
            $"authority={entry.Authority}{outcomeText}{noteText}";
        return new GovernanceEvent(subject, Kind: "trust", Summary: summary, At: entry.UpdatedAt);
    }
}
