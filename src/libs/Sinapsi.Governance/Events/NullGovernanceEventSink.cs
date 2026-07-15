namespace Sinapsi.Governance.Events;

/// <summary>The default no-op sink — used when the core runs without a bus (tests, cold start).</summary>
public sealed class NullGovernanceEventSink : IGovernanceEventSink
{
    public static readonly NullGovernanceEventSink Instance = new();
    private NullGovernanceEventSink() { }
    public void Emit(GovernanceEvent @event) { }
}
