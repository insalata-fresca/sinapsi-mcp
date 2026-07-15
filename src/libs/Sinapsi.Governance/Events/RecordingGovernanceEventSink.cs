namespace Sinapsi.Governance.Events;

/// <summary>
/// An in-memory sink that captures every emitted fact — for tests and for a host that
/// wants a local ring of recent governance facts. Each captured <see cref="GovernanceEvent"/>
/// is validated against the fact-not-trigger discipline on capture, so a mis-routed subject
/// fails loudly rather than silently landing on a command tree.
/// </summary>
public sealed class RecordingGovernanceEventSink : IGovernanceEventSink
{
    private readonly List<GovernanceEvent> _events = new();

    public IReadOnlyList<GovernanceEvent> Events => _events;

    public void Emit(GovernanceEvent @event)
    {
        GovernanceChannels.EnsureFact(@event.Subject);
        _events.Add(@event);
    }
}
