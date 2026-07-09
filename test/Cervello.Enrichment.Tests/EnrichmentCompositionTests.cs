using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 tests for <see cref="EnrichmentConfig"/> + the DI composition root
/// (<see cref="EnrichmentComposition"/>). The headline properties: the phase gate is ESCALATE-ONLY
/// by default (graded auto-apply OFF until the operator flips a config flag — the E5 gate), the
/// live-vs-fake seam is a config flag, and config is fail-closed. No network/DB — fake mode resolves
/// the storage tier offline.
/// </summary>
public sealed class EnrichmentCompositionTests
{
    private static EnrichmentConfig Cfg(IReadOnlyDictionary<string, string?>? env = null) =>
        EnrichmentConfig.From(env ?? new Dictionary<string, string?>());

    // ── phase gate ──────────────────────────────────────────────────────────────
    [Fact]
    public void Default_phase_is_escalate_only_the_e5_gate()
    {
        var cfg = Cfg();
        Assert.False(cfg.GradedAutoApply);

        var sp = new ServiceCollection().AddCervelloEnrichment(cfg).BuildServiceProvider();
        var policy = sp.GetRequiredService<DecisionPolicy>();
        Assert.Equal(PolicyPhase.EscalateOnly, policy.Phase);
    }

    // ── ratified base + graceful CT126 gates: both re-transcribe + re-ASR default OFF ──────────────
    [Fact]
    public void Base_retranscribe_and_reasr_default_off_so_ct126_is_not_a_drain_dependency()
    {
        var cfg = Cfg();
        Assert.False(cfg.BaseReTranscribeEnabled); // the Google .txt IS the base; no re-transcription
        Assert.False(cfg.ReAsrEnabled);            // selective re-ASR is a later, optional enhancement
    }

    [Fact]
    public void Base_retranscribe_and_reasr_are_enabled_only_by_config_no_code_change()
    {
        var cfg = Cfg(new Dictionary<string, string?>
        {
            ["CERVELLO_BASE_RETRANSCRIBE_ENABLED"] = "true",
            ["CERVELLO_REASR_ENABLED"] = "true",
        });
        Assert.True(cfg.BaseReTranscribeEnabled);
        Assert.True(cfg.ReAsrEnabled);
    }

    [Fact]
    public void A_clean_090_match_escalates_while_escalate_only_proving_the_gate()
    {
        var sp = new ServiceCollection().AddCervelloEnrichment(Cfg()).BuildServiceProvider();
        var policy = sp.GetRequiredService<DecisionPolicy>();

        var candidate = new AttributionCandidate("s1", "guilhem", 0.90, null, 0.0, PriorRelation.None, null);
        var verdict = policy.Decide(candidate, "rec-1");

        Assert.False(verdict.IsApplied);                             // NOT auto-applied
        Assert.Equal(AttributionOutcome.OpenPoint, verdict.Outcome); // escalated to an open-point
    }

    [Fact]
    public void Graded_auto_apply_is_enabled_only_by_config_no_code_change()
    {
        var cfg = Cfg(new Dictionary<string, string?> { ["CERVELLO_GRADED_AUTO_APPLY"] = "true" });
        var sp = new ServiceCollection().AddCervelloEnrichment(cfg).BuildServiceProvider();
        var policy = sp.GetRequiredService<DecisionPolicy>();

        Assert.Equal(PolicyPhase.GradedAutoApply, policy.Phase);

        var candidate = new AttributionCandidate("s1", "guilhem", 0.90, null, 0.0, PriorRelation.None, null);
        var verdict = policy.Decide(candidate, "rec-1");
        Assert.True(verdict.IsApplied);                                 // now the auto band applies
    }

    // ── live vs fake seam ────────────────────────────────────────────────────────
    [Fact]
    public void Fake_mode_wires_in_memory_stores_and_resolves_offline()
    {
        var sp = new ServiceCollection().AddCervelloEnrichment(Cfg()).BuildServiceProvider();

        Assert.IsType<InMemoryVoiceprintStore>(sp.GetRequiredService<IVoiceprintStore>());
        Assert.IsType<InMemoryOpenPointStore>(sp.GetRequiredService<IOpenPointStore>());
        Assert.IsType<InMemoryCorrectionMapStore>(sp.GetRequiredService<ICorrectionMapStore>());
        Assert.IsType<InMemoryEnrichmentLedger>(sp.GetRequiredService<IEnrichmentLedger>());
        Assert.IsType<StaticBearerProvider>(sp.GetRequiredService<IBearerProvider>());
        // No live git egress offline — the searchable-substrate publisher is a no-op.
        Assert.IsType<NoOpGitPublisher>(sp.GetRequiredService<IGitPublisher>());
    }

    [Fact]
    public void Live_mode_wires_the_live_pg_stores_and_the_audience_routing_bearer()
    {
        // Live wiring must RESOLVE without touching any endpoint (construction only). Provide the
        // OIDC env the AgentJwt options + the external-blob fetcher seam the pin store needs, plus the
        // static brain bearer the enrich routes require (composition fails closed on an empty one).
        var env = new Dictionary<string, string?>
        {
            ["CERVELLO_USE_LIVE_ADAPTERS"] = "true",
            ["CERVELLO_DB_PASSWORD"] = "unused-at-construction",
            ["CERVELLO_BRAIN_BEARER_TOKEN"] = "brain-bearer-under-test",
        };
        Environment.SetEnvironmentVariable("OIDC_ISSUER", "https://id.test");
        Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", "proj-1");
        try
        {
            var services = new ServiceCollection().AddCervelloEnrichment(EnrichmentConfig.From(env));
            // The pin store needs a live external-blob fetcher (a deploy-time seam) — supply a stub so
            // the graph resolves; a missing one is a loud deploy error, never a fake success.
            services.AddSingleton<IExternalBlobFetcher>(new NullBlobFetcher());
            var sp = services.BuildServiceProvider();

            Assert.IsType<PgVoiceprintStore>(sp.GetRequiredService<IVoiceprintStore>());
            Assert.IsType<PgOpenPointStore>(sp.GetRequiredService<IOpenPointStore>());
            Assert.IsType<PgCorrectionMapStore>(sp.GetRequiredService<ICorrectionMapStore>());
            Assert.IsType<PgEnrichmentLedger>(sp.GetRequiredService<IEnrichmentLedger>());
            // The bearer is AUDIENCE-ROUTED: static brain bearer for brain-api, minted JWT elsewhere.
            Assert.IsType<AudienceRoutingBearerProvider>(sp.GetRequiredService<IBearerProvider>());
            // The typed HTTP clients resolve (constructed, no call made).
            Assert.IsType<GatewayDiarizeEmbedClient>(sp.GetRequiredService<IDiarizeEmbedClient>());
            Assert.IsType<Ct126TranscribeClient>(sp.GetRequiredService<ITranscribeClient>());
            Assert.IsType<Ct126ReAsrClient>(sp.GetRequiredService<IReAsrClient>());
            Assert.IsType<BrainApiCorrectionLlm>(sp.GetRequiredService<ICorrectionLlm>());
            Assert.IsType<ForgejoMapPrWriter>(sp.GetRequiredService<IMapPrWriter>());
            // The searchable-substrate publisher is the LIVE forgejo contents pusher (recall fix §1).
            Assert.IsType<ForgejoContentsPublisher>(sp.GetRequiredService<IGitPublisher>());
            Assert.IsType<CtPinStore>(sp.GetRequiredService<IPinStore>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OIDC_ISSUER", null);
            Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", null);
        }
    }

    // ── the enrich routes require the static brain bearer: live mode FAILS CLOSED on an empty one ──
    [Fact]
    public void Live_mode_fails_closed_when_the_static_brain_bearer_is_empty()
    {
        // Live adapters enabled but CERVELLO_BRAIN_BEARER_TOKEN unset — the brain-api /v1/enrich/*
        // routes validate by string-equality against a static token, so an empty one is never valid.
        var env = new Dictionary<string, string?>
        {
            ["CERVELLO_USE_LIVE_ADAPTERS"] = "true",
            ["CERVELLO_DB_PASSWORD"] = "unused-at-construction",
            // CERVELLO_BRAIN_BEARER_TOKEN deliberately absent
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCervelloEnrichment(EnrichmentConfig.From(env)));
        Assert.Contains("CERVELLO_BRAIN_BEARER_TOKEN", ex.Message);
    }

    // ── the audience router: static brain bearer for brain-api, minted JWT for everything else ─────
    [Fact]
    public async Task Audience_router_returns_the_static_brain_bearer_only_for_the_brain_api_audience()
    {
        var router = new AudienceRoutingBearerProvider(
            brainApi: new StaticBearerProvider("STATIC-BRAIN"),
            minted: new StaticBearerProvider("MINTED-JWT"));

        // The three brain-api enrich clients tag "brain-api" → the static token.
        Assert.Equal("STATIC-BRAIN", await router.GetBearerAsync(AudienceRoutingBearerProvider.BrainApiAudience));
        Assert.Equal("STATIC-BRAIN", await router.GetBearerAsync(GatewayDiarizeEmbedClient.Audience));
        Assert.Equal("STATIC-BRAIN", await router.GetBearerAsync(BrainApiCorrectionLlm.Audience));
        Assert.Equal("STATIC-BRAIN", await router.GetBearerAsync(BrainApiRecordingFactSource.Audience));

        // CT126 + forgejo egress → the minted JWT (unchanged).
        Assert.Equal("MINTED-JWT", await router.GetBearerAsync(Ct126TranscribeClient.Audience));
        Assert.Equal("MINTED-JWT", await router.GetBearerAsync(Ct126ReAsrClient.Audience));
        Assert.Equal("MINTED-JWT", await router.GetBearerAsync(ForgejoMapPrWriter.Audience));
    }

    // ── fail-closed config ───────────────────────────────────────────────────────
    [Fact]
    public void Config_is_fail_closed_on_a_bad_bool_and_a_bad_url()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EnrichmentConfig.From(new Dictionary<string, string?> { ["CERVELLO_USE_LIVE_ADAPTERS"] = "yes-please" }));
        Assert.Throws<InvalidOperationException>(() =>
            EnrichmentConfig.From(new Dictionary<string, string?> { ["CERVELLO_BRAIN_API_BASE_URL"] = "not-a-url" }));
    }

    [Fact]
    public void Map_pr_defaults_to_dry_run_the_l1_boundary()
    {
        Assert.True(Cfg().MapPrDryRun);   // no real map-PR unless explicitly flipped at L2
    }

    private sealed class NullBlobFetcher : IExternalBlobFetcher
    {
        public Task<ReadOnlyMemory<byte>> FetchAsync(string externalRef, CancellationToken ct = default) =>
            throw new NotSupportedException("live fetch is an L2 concern");
    }
}
