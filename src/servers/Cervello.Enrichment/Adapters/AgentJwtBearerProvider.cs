using Cervello.Enrichment.Ports;
using Sinapsi.AgentJwt;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IBearerProvider"/> that mints an agent-scoped OIDC bearer at runtime via
/// <see cref="AgentJwtMinter"/> — the SAME agent-free pattern the Cervello.Watcher uses. The JWK
/// for <see cref="EnrichmentConfig.EnrichmentAgent"/> is provisioned on-CT by the
/// deploy-controller / Infisical <c>/ct146/cervello/</c> pattern; this process never holds a
/// long-lived secret and no bearer ever leaves the CT except as the outbound <c>Authorization</c>
/// header. The minter caches + refreshes the token internally (TTL - 1 min safety margin).
///
/// <para>The audience is currently informational (all engine egress rides the single scoped
/// enrichment identity through the agentgateway); it is threaded so a future per-audience mint is
/// a config change, not a code change.</para>
/// </summary>
public sealed class AgentJwtBearerProvider(AgentJwtMinter minter, EnrichmentConfig cfg) : IBearerProvider
{
    private readonly AgentJwtMinter _minter = minter ?? throw new ArgumentNullException(nameof(minter));
    private readonly EnrichmentConfig _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

    public Task<string> GetBearerAsync(string audience, CancellationToken ct = default) =>
        _minter.MintAsync(_cfg.EnrichmentAgent, ct);
}
