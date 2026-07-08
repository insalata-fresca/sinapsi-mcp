namespace Cervello.Enrichment.Ports;

/// <summary>
/// Supplies the outbound bearer token for the engine's HTTP adapters, keyed by logical
/// <paramref name="audience"/> (brain-api diarize-embed + correction + derive-facts, CT126
/// transcribe/re-ASR, forgejo map-PR). The token is resolved at RUNTIME on-CT and is
/// AUDIENCE-ROUTED in live mode by <see cref="Adapters.AudienceRoutingBearerProvider"/>:
/// <list type="bullet">
/// <item>the brain-api <c>/v1/enrich/*</c> audience gets a STATIC bearer (== brain-api's
///   <c>BRAIN_BEARER_TOKEN</c>) — those routes validate by plain string-equality, NOT the minted JWT;</item>
/// <item>CT126 + forgejo egress get an agent-scoped OIDC token minted via
///   <c>Sinapsi.AgentJwt.AgentJwtMinter</c> (JWK provisioned agent-free from Infisical
///   <c>/ct146/cervello/</c>).</item>
/// </list>
/// NO bearer or secret ever enters agent context or source. A fake supplies a static token in tests
/// (no mint, no network).
///
/// <para>This is the seam that keeps the L1/L2 boundary clean: L1 builds + unit-tests the adapters
/// against a static-token fake (asserting the <c>Authorization: Bearer</c> header shape); L2 wires
/// the live providers on-CT (static brain bearer + real-JWK <see cref="Adapters.AgentJwtBearerProvider"/>).</para>
/// </summary>
public interface IBearerProvider
{
    /// <summary>The bearer token to present, for the given logical audience (brain-api / ct126 / forgejo).</summary>
    Task<string> GetBearerAsync(string audience, CancellationToken ct = default);
}
