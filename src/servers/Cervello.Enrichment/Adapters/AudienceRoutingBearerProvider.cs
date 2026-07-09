using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// An <see cref="IBearerProvider"/> that ROUTES by logical audience: the brain-api
/// <c>/v1/enrich/*</c> routes get a STATIC brain bearer; forgejo egress gets a STATIC forgejo access
/// token; everything else (CT126 speaches) gets the minted agent-scoped JWT.
///
/// <para><b>Why brain-api is static.</b> brain-api's enrich routes
/// (<c>/v1/enrich/diarize-embed|correct|derive-facts</c>) validate the presented bearer by plain
/// string-equality against a static <c>BRAIN_BEARER_TOKEN</c> (<c>VerifyBearer</c>) — they do NOT run
/// Zitadel JWT validation, which is wired only to <c>/v1/sessions</c> (and whose agent-map excludes
/// <c>agent-cervello-enrichment</c>). So the minted JWT is rejected there; the engine must present the
/// SAME static token brain-api holds.</para>
///
/// <para><b>Why forgejo is static too.</b> Forgejo's REST API (<c>/api/v1/repos/.../contents/...</c>,
/// used by both the searchable-transcript git push and the map-PR writer) accepts a native forgejo
/// ACCESS TOKEN, NOT a Zitadel OIDC JWT — presenting the minted <c>agent-cervello-enrichment</c> JWT
/// there is REJECTED with 401 (the failure that kept transcripts out of <c>ste/cervello</c> and left
/// recall empty). So the forgejo audience rides a static forgejo access token
/// (<see cref="EnrichmentConfig.ForgejoRepoToken"/> == Infisical <c>/ct146/cervello/FORGE_REPO_TOKEN</c>).</para>
///
/// <para><b>CT126 keeps the minted JWT.</b> CT126 speaches egress rides the scoped enrichment identity
/// through the agentgateway and still needs the minted JWT (its own <c>ct126-speaches</c> audience
/// falls through to <c>_minted</c>). This provider keeps all three live without any adapter code change
/// — the clients already tag their egress with <see cref="BrainApiAudience"/> / <see cref="ForgejoAudience"/>
/// / their own audience.</para>
///
/// <para>Secrets are agent-free: the static brain bearer is <see cref="EnrichmentConfig.BrainBearerToken"/>
/// (Infisical <c>/ct121/homelab-state-mcp/BRAIN_BEARER_TOKEN</c>), the static forgejo token is
/// <see cref="EnrichmentConfig.ForgejoRepoToken"/> (Infisical <c>/ct146/cervello/FORGE_REPO_TOKEN</c>),
/// the minted JWT comes from the on-CT JWK — NONE enters source or agent context. Scoped-JWT acceptance
/// (per-route audience acceptance on brain-api / forgejo) is a noted FUTURE hardening; the static
/// bearers are the smallest by-the-books fix.</para>
/// </summary>
public sealed class AudienceRoutingBearerProvider : IBearerProvider
{
    /// <summary>The logical audience the three brain-api enrich clients tag their egress with.</summary>
    public const string BrainApiAudience = "brain-api";

    /// <summary>The logical audience the forgejo egress (git push + map-PR writer) tags its egress with.</summary>
    public const string ForgejoAudience = "forgejo";

    private readonly IBearerProvider _brainApi;
    private readonly IBearerProvider _forgejo;
    private readonly IBearerProvider _minted;

    /// <param name="brainApi">Supplies the static brain-api bearer for the <see cref="BrainApiAudience"/>.</param>
    /// <param name="forgejo">Supplies the static forgejo access token for the <see cref="ForgejoAudience"/>.</param>
    /// <param name="minted">Supplies the minted agent JWT for every OTHER audience (CT126 speaches).</param>
    public AudienceRoutingBearerProvider(IBearerProvider brainApi, IBearerProvider forgejo, IBearerProvider minted)
    {
        _brainApi = brainApi ?? throw new ArgumentNullException(nameof(brainApi));
        _forgejo = forgejo ?? throw new ArgumentNullException(nameof(forgejo));
        _minted = minted ?? throw new ArgumentNullException(nameof(minted));
    }

    public Task<string> GetBearerAsync(string audience, CancellationToken ct = default)
    {
        if (string.Equals(audience, BrainApiAudience, StringComparison.Ordinal))
            return _brainApi.GetBearerAsync(audience, ct);
        if (string.Equals(audience, ForgejoAudience, StringComparison.Ordinal))
            return _forgejo.GetBearerAsync(audience, ct);
        return _minted.GetBearerAsync(audience, ct);
    }
}
