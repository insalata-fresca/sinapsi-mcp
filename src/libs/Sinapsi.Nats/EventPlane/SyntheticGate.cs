namespace Sinapsi.Nats.EventPlane;

/// <summary>Which traffic a freshly-acted change is exposed to.</summary>
public enum TrafficPhase
{
    /// <summary>Only SYNTHETIC probes touch the change — no real user/caller traffic yet. This is the
    /// mandatory first phase after an act (home-server <c>docs/64 §3</c>): prove it on synthetic
    /// monitoring before real traffic can reach it.</summary>
    SyntheticOnly,
    /// <summary>Synthetic checks passed across the bake window — real traffic may be admitted.</summary>
    RealTrafficAdmitted,
}

/// <summary>One synthetic monitoring probe result against the changed surface (a canary request, a
/// health check, a smoke test) — deliberately NOT real traffic.</summary>
/// <param name="Name">Probe label.</param>
/// <param name="Passed">Whether the synthetic probe passed.</param>
/// <param name="Detail">Optional context.</param>
public sealed record SyntheticProbeResult(string Name, bool Passed, string? Detail = null);

/// <summary>The gate decision: whether real traffic may be admitted yet, and if not, why not.</summary>
/// <param name="Phase">The phase the change is allowed to be in.</param>
/// <param name="AdmitRealTraffic">True only when every synthetic probe passed AND the bake window is
/// satisfied AND at least one probe actually ran.</param>
/// <param name="Blockers">The reasons real traffic is withheld (empty iff admitted).</param>
public sealed record SyntheticGateDecision(TrafficPhase Phase, bool AdmitRealTraffic, IReadOnlyList<string> Blockers);

/// <summary>
/// The synthetic-monitoring-only gate (home-server <c>docs/64 §3</c>): after an act, a change lives in
/// a SYNTHETIC-only phase and may only be promoted to real traffic once every synthetic probe passes
/// across the bake window. "No probes ran" is itself a blocker — an empty synthetic suite is not a
/// pass (that would be admitting real traffic on zero evidence). Composes with
/// <see cref="PostActionVerifier"/>: the caller passes the bake result in as
/// <paramref name="bakeWindowSatisfied"/>.
/// </summary>
public static class SyntheticGate
{
    /// <summary>Decide whether real traffic may be admitted. Fail-closed: any failed probe, an empty
    /// probe set, or an unsatisfied bake window keeps the change <see cref="TrafficPhase.SyntheticOnly"/>.</summary>
    public static SyntheticGateDecision Evaluate(IReadOnlyList<SyntheticProbeResult> probes, bool bakeWindowSatisfied)
    {
        ArgumentNullException.ThrowIfNull(probes);

        var blockers = new List<string>();

        if (probes.Count == 0)
            blockers.Add("no synthetic probes ran — an empty synthetic suite is not a pass; real traffic needs positive evidence");

        foreach (var p in probes)
            if (!p.Passed)
                blockers.Add($"synthetic probe '{p.Name}' failed{(string.IsNullOrEmpty(p.Detail) ? "" : $": {p.Detail}")}");

        if (!bakeWindowSatisfied)
            blockers.Add("bake window not yet satisfied — synthetic checks must hold across the holding window before real traffic");

        return blockers.Count == 0
            ? new SyntheticGateDecision(TrafficPhase.RealTrafficAdmitted, true, Array.Empty<string>())
            : new SyntheticGateDecision(TrafficPhase.SyntheticOnly, false, blockers);
    }
}
