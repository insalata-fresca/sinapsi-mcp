namespace Sinapsi.Nats.EventPlane;

/// <summary>Where a piece of trust-plane state (an OpenFGA relation, a capability grant) was
/// read from when an enforcement decision depends on it.</summary>
public enum TrustStateSource
{
    /// <summary>Read directly from the authoritative store (OpenFGA <c>/check</c>, the live
    /// capability model). This is the ONLY source an enforcement decision may use.</summary>
    AuthoritativeStore,

    /// <summary>Read from a lagging bus/materialized projection of the trust plane (a NATS
    /// KV mirror, a cached read-model, a replayed event stream). Eventual consistency has no
    /// safety property — a stale projection can authorize a just-revoked relation.</summary>
    BusProjection,
}

/// <summary>
/// Guards the canon's source-of-truth rule (home-server <c>docs/64 §3</c>): "enforcement reads
/// the SOURCE OF TRUTH (OpenFGA), not a lagging bus projection (eventual consistency has no
/// safety property)."
///
/// <para>Any component that turns trust-plane state into an ALLOW/DENY must call
/// <see cref="RequireAuthoritative"/> with the source it actually read. A
/// <see cref="TrustStateSource.BusProjection"/> for an enforcement decision throws — so the
/// future auto-merge/deploy act-path cannot be built to enforce off a materialized projection
/// by accident. The Q1 PDP already honours this: it reads OpenFGA <c>/check</c> live
/// (<see cref="TrustStateSource.AuthoritativeStore"/>); a projection is legitimate only for
/// DISPLAY (the Sentinel read-model), never for a gate.</para>
/// </summary>
public static class TrustPlaneReadGuard
{
    /// <summary>Assert that an enforcement decision named <paramref name="decisionKind"/> read
    /// trust-plane state from the authoritative store. Throws on a projection source.</summary>
    /// <exception cref="InvalidOperationException">the source is a lagging projection.</exception>
    public static void RequireAuthoritative(TrustStateSource source, string decisionKind)
    {
        if (source == TrustStateSource.BusProjection)
            throw new InvalidOperationException(
                $"enforcement decision '{decisionKind}' read trust-plane state from a BUS PROJECTION. " +
                "Eventual consistency has no safety property — read the authoritative store " +
                "(OpenFGA /check) instead. A projection is for display only. See docs/64 §3.");
    }

    /// <summary>Non-throwing form: true when <paramref name="source"/> is safe to enforce on.</summary>
    public static bool IsAuthoritative(TrustStateSource source) => source == TrustStateSource.AuthoritativeStore;
}
