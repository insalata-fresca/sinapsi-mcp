using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// An <see cref="IBearerProvider"/> that ROUTES by logical audience: the brain-api
/// <c>/v1/enrich/*</c> routes get a STATIC bearer, everything else (CT126 speaches, forgejo) gets the
/// minted agent-scoped JWT.
///
/// <para><b>Why.</b> brain-api's enrich routes (<c>/v1/enrich/diarize-embed|correct|derive-facts</c>)
/// validate the presented bearer by plain string-equality against a static
/// <c>BRAIN_BEARER_TOKEN</c> (<c>VerifyBearer</c>) — they do NOT run Zitadel JWT validation, which is
/// wired only to <c>/v1/sessions</c> (and whose agent-map excludes
/// <c>agent-cervello-enrichment</c>). So the minted JWT is rejected there; the engine must present
/// the SAME static token brain-api holds. CT126 + forgejo egress ride the scoped enrichment identity
/// through the agentgateway and still need the minted JWT. This provider keeps both live without any
/// adapter code change — the three enrich clients already tag their egress with
/// <see cref="BrainApiAudience"/>.</para>
///
/// <para>Secrets are agent-free: the static brain bearer is <see cref="EnrichmentConfig.BrainBearerToken"/>
/// (Infisical <c>/ct121/homelab-state-mcp/BRAIN_BEARER_TOKEN</c>), the minted JWT comes from the on-CT
/// JWK — NEITHER enters source or agent context. Scoped-JWT-for-enrich (per-route audience acceptance
/// on brain-api) is a noted FUTURE hardening; this static-bearer route is the smallest by-the-books fix.</para>
/// </summary>
public sealed class AudienceRoutingBearerProvider : IBearerProvider
{
    /// <summary>The logical audience the three brain-api enrich clients tag their egress with.</summary>
    public const string BrainApiAudience = "brain-api";

    private readonly IBearerProvider _brainApi;
    private readonly IBearerProvider _minted;

    /// <param name="brainApi">Supplies the static brain-api bearer for the <see cref="BrainApiAudience"/>.</param>
    /// <param name="minted">Supplies the minted agent JWT for every OTHER audience (CT126 / forgejo).</param>
    public AudienceRoutingBearerProvider(IBearerProvider brainApi, IBearerProvider minted)
    {
        _brainApi = brainApi ?? throw new ArgumentNullException(nameof(brainApi));
        _minted = minted ?? throw new ArgumentNullException(nameof(minted));
    }

    public Task<string> GetBearerAsync(string audience, CancellationToken ct = default) =>
        string.Equals(audience, BrainApiAudience, StringComparison.Ordinal)
            ? _brainApi.GetBearerAsync(audience, ct)
            : _minted.GetBearerAsync(audience, ct);
}
