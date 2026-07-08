using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.DependencyInjection;
using Sinapsi.AgentJwt;

namespace Cervello.Enrichment;

/// <summary>
/// The enrichment engine's DI COMPOSITION ROOT (L1). One entry point,
/// <see cref="AddCervelloEnrichment"/>, wires every port to either its LIVE adapter or its in-memory
/// FAKE — the choice is <see cref="EnrichmentConfig.UseLiveAdapters"/>, a CONFIG FLAG, never a code
/// change (the design mandate: "switching is configuration"). A host (the deploy slice) calls this
/// once; L1 tests call it with a fake-wired config to prove the graph resolves.
///
/// <para><b>Phase gate — escalate-only by default (the E5 gate).</b> The <see cref="DecisionPolicy"/>
/// is registered with <see cref="PolicyPhase.EscalateOnly"/> unless
/// <see cref="EnrichmentConfig.GradedAutoApply"/> is explicitly true. Graded auto-apply stays OFF
/// until the operator's held-out validation passes — flipping it is a config change
/// (<c>CERVELLO_GRADED_AUTO_APPLY=true</c>), never a code change. This is the "batch auto-apply stays
/// dark until validated" decision, enforced at the composition seam.</para>
///
/// <para><b>Secrets are agent-free.</b> In live mode the bearer is minted at runtime by
/// <see cref="AgentJwtMinter"/> (JWK provisioned on-CT via Infisical <c>/ct146/cervello/</c>); the
/// Postgres password arrives via <c>CERVELLO_DB_PASSWORD</c> at deploy. NEITHER ever enters agent
/// context or source. In fake mode a <see cref="StaticBearerProvider"/> supplies a placeholder token
/// (no mint, no network) so the graph resolves offline.</para>
/// </summary>
public static class EnrichmentComposition
{
    /// <summary>
    /// Wire the enrichment engine into <paramref name="services"/> from <paramref name="cfg"/>.
    /// Live adapters when <see cref="EnrichmentConfig.UseLiveAdapters"/>; fakes otherwise. The
    /// escalate-only phase gate is applied unless graded auto-apply is explicitly enabled.
    /// </summary>
    public static IServiceCollection AddCervelloEnrichment(this IServiceCollection services, EnrichmentConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cfg);

        services.AddSingleton(cfg);

        // ── Policy + phase gate (identical in live + fake mode — it is pure logic) ──────────────
        var phase = cfg.GradedAutoApply ? PolicyPhase.GradedAutoApply : PolicyPhase.EscalateOnly;
        services.AddSingleton(new DecisionPolicy(DecisionBands.Default, phase));

        // The §10 enrollment allowlist is DATA (who consented), injected — never inferred. Default
        // Empty (nobody enrollable) until the deploy supplies the consented set; overriding is a
        // registration replacement by the host, not a code change here.
        services.AddSingleton(EnrollmentAllowlist.Empty);

        if (cfg.UseLiveAdapters)
            AddLiveAdapters(services, cfg);
        else
            AddFakeAdapters(services);

        return services;
    }

    /// <summary>Wire the LIVE adapters (brain-api / CT126 / CT146 pgvector / forgejo). Deploy slice / L2.</summary>
    private static void AddLiveAdapters(IServiceCollection services, EnrichmentConfig cfg)
    {
        var timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds);

        // Bearer minting (agent-free) — the SAME pattern the Watcher uses.
        services.AddSingleton(AgentJwtOptions.FromEnvironment());
        services.AddHttpClient<AgentJwtMinter>();
        services.AddSingleton<IBearerProvider, AgentJwtBearerProvider>();

        // brain-api typed clients (diarize-embed proxy + correction LLM).
        services.AddHttpClient<IDiarizeEmbedClient, GatewayDiarizeEmbedClient>(c =>
        {
            c.BaseAddress = new Uri(cfg.BrainApiBaseUrl);
            c.Timeout = timeout;
        });
        services.AddHttpClient<ICorrectionLlm, BrainApiCorrectionLlm>(c =>
        {
            c.BaseAddress = new Uri(cfg.BrainApiBaseUrl);
            c.Timeout = timeout;
        });

        // CT126 speaches typed clients (base transcribe + selective re-ASR).
        services.AddHttpClient<ITranscribeClient, Ct126TranscribeClient>(c =>
        {
            c.BaseAddress = new Uri(cfg.Ct126BaseUrl);
            c.Timeout = timeout;
        });
        services.AddHttpClient<IReAsrClient, Ct126ReAsrClient>(c =>
        {
            c.BaseAddress = new Uri(cfg.Ct126BaseUrl);
            c.Timeout = timeout;
        });

        // forgejo map-PR writer (dry-run aware — no real PR unless CERVELLO_MAP_PR_DRY_RUN=false).
        services.AddHttpClient<IMapPrWriter, ForgejoMapPrWriter>(c =>
        {
            c.BaseAddress = new Uri(cfg.ForgejoBaseUrl);
            c.Timeout = timeout;
        });

        // CT146 Postgres stores (pgvector voiceprints, correction-map, open-points, ledger).
        services.AddSingleton<IVoiceprintStore, PgVoiceprintStore>();
        services.AddSingleton<ICorrectionMapStore, PgCorrectionMapStore>();
        services.AddSingleton<IOpenPointStore, PgOpenPointStore>();
        services.AddSingleton<IEnrichmentLedger, PgEnrichmentLedger>();

        // CT-side + git-working-tree stores. The repo working tree + pin/log dirs default to the
        // Watcher's custody root; a host overrides via the matching env before calling.
        var repoRoot = Environment.GetEnvironmentVariable("CERVELLO_REPO_WORKTREE")
                       ?? "/var/lib/cervello/repo";
        var pinDir = Environment.GetEnvironmentVariable("CERVELLO_PIN_DIR")
                     ?? "/var/lib/cervello/pins";
        var accessLogPath = Environment.GetEnvironmentVariable("CERVELLO_ACCESS_LOG")
                            ?? "/var/lib/cervello/access.log";

        services.AddSingleton<ITranscriptStore>(_ => new RepoTranscriptStore(repoRoot));
        services.AddSingleton<IBundleStore>(_ => new RepoBundleStore(repoRoot));
        services.AddSingleton<ILinkResolver>(_ => new RepoLinkResolver(repoRoot));
        services.AddSingleton<IAccessLog>(_ => new CtAccessLog(accessLogPath));

        // Pin store: the on-CT blob write + sha are live; the external fetch is an L2 seam (a host
        // registers the live IExternalBlobFetcher over the gateway). Registered here so the graph
        // resolves; a missing fetcher is a deploy-time wiring error, surfaced loudly, not a fake.
        services.AddSingleton<IPinStore>(sp =>
            new CtPinStore(sp.GetRequiredService<IExternalBlobFetcher>(), pinDir));

        // Enrollment-source provider (transient diarized centroids, CT-side).
        services.AddSingleton<DiarizedCentroidEnrollmentSourceProvider>();
        services.AddSingleton<IEnrollmentSourceProvider>(sp =>
            sp.GetRequiredService<DiarizedCentroidEnrollmentSourceProvider>());

        // Open-points auth gate (bearer-gated private-plane surface).
        AddAuthGate(services, cfg);

        // The graph-writer composes the map review-PR from applied facts + self-lints before OpenPr.
        services.AddSingleton<CervelloGraphWriter>();
    }

    /// <summary>Wire the in-memory FAKES (offline slice / L1 tests). Deterministic, no network, no DB.</summary>
    private static void AddFakeAdapters(IServiceCollection services)
    {
        services.AddSingleton<IBearerProvider>(new StaticBearerProvider("fake-bearer"));

        // NOTE: the diarize-embed / transcribe / re-ASR / correction-LLM / map-PR ports have NO
        // in-engine fake (their fakes live in the TEST project, by design). In fake mode a host that
        // needs those wired supplies them; the DI root registers only the fakes that ship in the
        // engine assembly (the stores + CT-side seams), so a fake-mode graph resolves the storage
        // tier offline. This keeps the engine free of test doubles while proving the wiring.
        services.AddSingleton<IVoiceprintStore>(sp =>
            new InMemoryVoiceprintStore(sp.GetRequiredService<EnrollmentAllowlist>()));
        services.AddSingleton<ICorrectionMapStore, InMemoryCorrectionMapStore>();
        services.AddSingleton<IOpenPointStore, InMemoryOpenPointStore>();
        services.AddSingleton<IEnrichmentLedger, InMemoryEnrichmentLedger>();
        services.AddSingleton<IBundleStore, InMemoryBundleStore>();

        AddAuthGate(services, EnrichmentConfig.From(_ => null) with { OpenPointsAuthEnabled = true });
    }

    private static void AddAuthGate(IServiceCollection services, EnrichmentConfig cfg)
    {
        // The open-points bearer gate. In prod the token is the operator's cervello bearer, read
        // agent-free from the environment at runtime. The gate FAILS CLOSED on an empty expected
        // token (a mis-provisioned deploy never exposes the tools open) — so we pass null when auth
        // is disabled only for a test that wires its own gate. Never a hard-coded prod token here.
        _ = cfg; // auth-enabled posture is carried by whether CERVELLO_OPEN_POINTS_TOKEN is set
        var token = Environment.GetEnvironmentVariable("CERVELLO_OPEN_POINTS_TOKEN");
        services.AddSingleton<IOpenPointsAuthGate>(new TokenOpenPointsAuthGate(token));
    }
}

/// <summary>
/// A static <see cref="IBearerProvider"/> for the offline slice + tests: returns a fixed placeholder
/// token, no mint, no network. NEVER used in live mode (the live provider mints via AgentJwt).
/// </summary>
public sealed class StaticBearerProvider(string token) : IBearerProvider
{
    private readonly string _token = string.IsNullOrEmpty(token)
        ? throw new ArgumentException("token must be non-empty", nameof(token))
        : token;

    public Task<string> GetBearerAsync(string audience, CancellationToken ct = default) => Task.FromResult(_token);
}
