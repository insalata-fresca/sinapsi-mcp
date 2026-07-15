namespace Sinapsi.Governance.Events;

/// <summary>
/// The seam by which governance FACTS leave the pure core and reach the bus / console.
/// The core (trust ledger, SLO, sampler) depends only on this port, so its logic stays
/// deterministic + unit-testable; a NATS-backed implementation (publishing CloudEvents
/// under <see cref="GovernanceChannels.FactSubjectRoot"/>) is wired by the host and is
/// deferred here — this library ships the port plus in-memory implementations.
/// </summary>
public interface IGovernanceEventSink
{
    /// <summary>Publish one governance fact. Must never block the caller on I/O errors —
    /// a fact-emission failure is not allowed to change a trust decision.</summary>
    void Emit(GovernanceEvent @event);
}
