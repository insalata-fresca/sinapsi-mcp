namespace Cervello.Enrichment.Ports;

/// <summary>
/// Supplies the outbound bearer token for the engine's HTTP adapters (brain-api diarize-embed +
/// correction, CT126 transcribe/re-ASR, forgejo map-PR). The token is resolved at RUNTIME on-CT —
/// the live adapter mints an agent-scoped OIDC token via <c>Sinapsi.AgentJwt.AgentJwtMinter</c>
/// (JWK provisioned agent-free from Infisical <c>/ct146/cervello/</c>), so NO bearer or secret ever
/// enters agent context or source. A fake supplies a static token in tests (no mint, no network).
///
/// <para>This is the seam that keeps the L1/L2 boundary clean: L1 builds + unit-tests the adapters
/// against a static-token fake (asserting the <c>Authorization: Bearer</c> header shape); L2 wires
/// the live <see cref="Adapters.AgentJwtBearerProvider"/> on-CT with the real JWK.</para>
/// </summary>
public interface IBearerProvider
{
    /// <summary>The bearer token to present, for the given logical audience (brain-api / ct126 / forgejo).</summary>
    Task<string> GetBearerAsync(string audience, CancellationToken ct = default);
}
