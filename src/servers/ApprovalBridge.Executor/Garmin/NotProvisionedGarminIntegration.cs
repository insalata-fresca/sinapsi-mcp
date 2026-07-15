using ApprovalBridge.Executor.Sdk;

namespace ApprovalBridge.Executor.Garmin;

/// <summary>
/// The default, INERT Garmin integration used when the executor is wired into the broker but the real Garmin
/// network integration + token store have not been provisioned (they are out of scope for the E1.4 shadow
/// slice — no real Garmin call, no live network). It refuses every exchange with a non-secret
/// <see cref="ExecutorException"/>, so even flipping the executor-live flag without provisioning the real
/// integration executes NOTHING — the deny-by-default posture holds one layer deeper than the broker's seam.
/// </summary>
public sealed class NotProvisionedGarminEndpoint : IGarminTokenEndpoint
{
    public Task<GarminToken> ExchangeAsync(string authCode, string clientSecret, CancellationToken ct = default) =>
        throw new ExecutorException("garmin token endpoint not provisioned (live Garmin integration is out of scope for the shadow slice)");
}

/// <summary>The inert companion store — never reached, since the endpoint refuses first.</summary>
public sealed class NotProvisionedGarminTokenStore : IGarminTokenStore
{
    public Task StoreAsync(GarminToken token, CancellationToken ct = default) =>
        throw new ExecutorException("garmin token store not provisioned");
}
