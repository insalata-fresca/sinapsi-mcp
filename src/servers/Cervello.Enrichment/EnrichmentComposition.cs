using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// <para><b>Secrets are agent-free.</b> In live mode the CT126 + forgejo bearer is minted at runtime
/// by <see cref="AgentJwtMinter"/> (JWK provisioned on-CT via Infisical <c>/ct146/cervello/</c>),
/// while the brain-api <c>/v1/enrich/*</c> routes get the STATIC <see cref="EnrichmentConfig.BrainBearerToken"/>
/// (== brain-api's <c>BRAIN_BEARER_TOKEN</c>, from Infisical <c>/ct121/homelab-state-mcp</c>) — routed
/// by <see cref="Adapters.AudienceRoutingBearerProvider"/>; the Postgres password arrives via
/// <c>CERVELLO_DB_PASSWORD</c> at deploy. NONE ever enters agent context or source. In fake mode a
/// <see cref="StaticBearerProvider"/> supplies a placeholder token (no mint, no network) so the graph
/// resolves offline.</para>
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
        // The SAME escalate-only-by-default phase gates BOTH the attribution decision policy and the
        // correction grader, so a clean 0.90 match still escalates (first concept: every band →
        // open-point) until CERVELLO_GRADED_AUTO_APPLY=true. Registered once here, shared by the stages.
        var phase = cfg.GradedAutoApply ? PolicyPhase.GradedAutoApply : PolicyPhase.EscalateOnly;
        services.AddSingleton(new DecisionPolicy(DecisionBands.Default, phase));
        services.AddSingleton(new CorrectionGrader(phase));

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

    /// <summary>
    /// Wire the FULL end-to-end pipeline: every stage (ingest → transcribe → diarize+embed →
    /// cluster-merge → attribute → correct → enrich+link → apply) + the
    /// <see cref="EnrichmentPipeline"/> orchestrator that threads them (MISSION E-PIPE). This is the
    /// piece the host runs to drive a <c>normalized</c> recording all the way to
    /// <c>graph_pr_opened</c>/escalated. It resolves the ports from <see cref="AddCervelloEnrichment"/>
    /// (which MUST be called first) — including the three orchestration input ports the chain needs
    /// but no single stage owns: <see cref="IAudioSource"/> (the transient audio the two audio stages
    /// consume), <see cref="IPriorSource"/> (the filename+manifest prior), and
    /// <see cref="IRecordingFactSource"/> (the derived summary/links/timeline/attention/participants/
    /// garbled-spans). In LIVE mode <see cref="AddCervelloEnrichment"/> now registers all three live
    /// adapters (L-SEAMS: <see cref="StagingBlobAudioSource"/> / <see cref="ManifestPriorSource"/> /
    /// <see cref="BrainApiRecordingFactSource"/>); in fake mode a test supplies them. Registering the
    /// stages here (not in every host) keeps the composition single-sourced.
    /// </summary>
    public static IServiceCollection AddCervelloPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Every stage is a thin, dependency-injected unit — its ports are already registered by
        // AddCervelloEnrichment (stores/clients) or by the host/test (audio + fact source + the
        // clients the engine assembly leaves to the caller).
        services.AddSingleton<IngestStage>();

        // BaseTranscribeStage: the Google .txt IS the base (IBaseTranscriptSource). The CT126
        // re-transcription client is an OPTIONAL fallback, wired ONLY when the config enables it
        // (CERVELLO_BASE_RETRANSCRIBE_ENABLED=true) — otherwise the stage never touches CT126 for the
        // base. Explicit factory so the optional client + flag are resolved from config.
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<EnrichmentConfig>();
            var reTranscribe = cfg.BaseReTranscribeEnabled
                ? sp.GetService<ITranscribeClient>()   // may be unregistered in fake mode → null → disabled
                : null;
            return new BaseTranscribeStage(
                sp.GetRequiredService<IBaseTranscriptSource>(),
                sp.GetRequiredService<ITranscriptStore>(),
                reTranscribe,
                cfg.BaseReTranscribeEnabled,
                sp.GetService<ILogger<BaseTranscribeStage>>());
        });

        services.AddSingleton<DiarizeEmbedStage>();
        services.AddSingleton<ClusterMergeStage>();
        services.AddSingleton<AttributionStage>();

        // CorrectionStage: selective re-ASR is OPTIONAL + graceful-degrade. The IReAsrClient is passed
        // ONLY when CERVELLO_REASR_ENABLED=true; otherwise garbled spans are omitted (left as-is) and
        // the drain never depends on CT126. Explicit factory so the optional client + flag resolve.
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<EnrichmentConfig>();
            var reAsr = cfg.ReAsrEnabled ? sp.GetService<IReAsrClient>() : null;
            return new CorrectionStage(
                sp.GetRequiredService<ICorrectionLlm>(),
                sp.GetRequiredService<ICorrectionMapStore>(),
                reAsr,
                sp.GetRequiredService<CorrectionGrader>(),
                cfg.ReAsrEnabled,
                sp.GetService<ILogger<CorrectionStage>>());
        });

        services.AddSingleton<EnrichLinkStage>();
        services.AddSingleton<ApplyStage>();

        services.AddSingleton<EnrichmentPipeline>();
        return services;
    }

    /// <summary>Wire the LIVE adapters (brain-api / CT126 / CT146 pgvector / forgejo). Deploy slice / L2.</summary>
    private static void AddLiveAdapters(IServiceCollection services, EnrichmentConfig cfg)
    {
        var timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds);

        // ── Outbound bearer (audience-routed) ─────────────────────────────────────────────────────
        // brain-api's /v1/enrich/* routes validate by plain string-equality against a STATIC
        // BRAIN_BEARER_TOKEN — they do NOT accept the minted agent-cervello-enrichment JWT (that
        // audience is wired only to /v1/sessions, whose agent-map excludes this identity). So the
        // brain-api audience presents the static brain bearer; CT126 + forgejo egress keep the minted
        // JWT (the SAME agent-free mint the Watcher uses). Fail CLOSED on an empty brain bearer — a
        // mis-provisioned deploy never presents an empty token (the enrich routes would 401 anyway).
        if (string.IsNullOrEmpty(cfg.BrainBearerToken))
            throw new InvalidOperationException(
                "CERVELLO_BRAIN_BEARER_TOKEN is empty but live adapters are enabled: the brain-api " +
                "/v1/enrich/* routes require the static brain bearer (== brain-api's BRAIN_BEARER_TOKEN, " +
                "injected agent-free from Infisical /ct121/homelab-state-mcp at deploy). Provide it, or " +
                "run with CERVELLO_USE_LIVE_ADAPTERS=false for the offline slice.");

        // forgejo egress (the searchable-transcript git push AND the map-PR writer) presents a native
        // forgejo ACCESS TOKEN — forgejo's REST API rejects the minted Zitadel JWT with 401 (the
        // failure that kept transcripts out of ste/cervello and left recall empty). Fail CLOSED on an
        // empty forgejo token — a mis-provisioned deploy never presents an empty token (the push would
        // 401 anyway).
        if (string.IsNullOrEmpty(cfg.ForgejoRepoToken))
            throw new InvalidOperationException(
                "CERVELLO_FORGE_REPO_TOKEN is empty but live adapters are enabled: the forgejo egress " +
                "(searchable-transcript git push + map-PR writer) requires a native forgejo access token " +
                "(== Infisical /ct146/cervello/FORGE_REPO_TOKEN, injected agent-free at deploy) — forgejo's " +
                "REST API rejects the minted Zitadel JWT with 401. Provide it, or run with " +
                "CERVELLO_USE_LIVE_ADAPTERS=false for the offline slice.");

        // The minted-JWT provider (CT126 speaches). Kept exactly as before — the Watcher's pattern.
        services.AddSingleton(AgentJwtOptions.FromEnvironment());
        services.AddHttpClient<AgentJwtMinter>();
        services.AddSingleton<AgentJwtBearerProvider>();

        // The audience router: static brain bearer for "brain-api", static forgejo access token for
        // "forgejo", minted JWT for everything else (CT126 speaches).
        services.AddSingleton<IBearerProvider>(sp => new AudienceRoutingBearerProvider(
            new StaticBearerProvider(cfg.BrainBearerToken),
            new StaticBearerProvider(cfg.ForgejoRepoToken),
            sp.GetRequiredService<AgentJwtBearerProvider>()));

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

        // brain-api recording-fact derivation (summary/links/timeline/attention/participants/garbled)
        // — mirrors the correction LLM; every derived fact is evidence-gated at the wire boundary
        // (unsourced → dropped, never invented). The attribution is NOT derived here.
        services.AddHttpClient<IRecordingFactSource, BrainApiRecordingFactSource>(c =>
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

        // Every live Pg store owns table(s) that MUST be created on startup. Register each as an
        // ISchemaInitializer (resolving the SAME singleton — no double-construction) so the host
        // ensures ALL adapter schemas, not just enrichment_ledger. (MIGRATE-FIX: the host used to
        // ensure only the ledger, so correction_map / voiceprints / open_points were never created
        // on a fresh CT146 and the CorrectionStage failed with 42P01.) The tables are independent
        // (no cross-adapter FKs), so any order is correct.
        services.AddSingleton<ISchemaInitializer>(sp => (PgEnrichmentLedger)sp.GetRequiredService<IEnrichmentLedger>());
        services.AddSingleton<ISchemaInitializer>(sp => (PgCorrectionMapStore)sp.GetRequiredService<ICorrectionMapStore>());
        services.AddSingleton<ISchemaInitializer>(sp => (PgVoiceprintStore)sp.GetRequiredService<IVoiceprintStore>());
        services.AddSingleton<ISchemaInitializer>(sp => (PgOpenPointStore)sp.GetRequiredService<IOpenPointStore>());

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

        // forgejo searchable-substrate publisher: commits transcripts + bundles + manifest to
        // ste/cervello main via the contents REST API (the SAME agent-free bearer egress), so the
        // strictly-git-sourced indexer (:8009) indexes recording content → recall returns it. This is
        // INDEPENDENT of the map-PR dry-run gate (that governs map/ attributions); audio + voiceprints
        // never enter git (LINT R7, enforced in the publisher). Reads from the CT working tree above.
        // A named HttpClient supplies the base address + timeout; the factory injects the working tree.
        services.AddHttpClient(nameof(ForgejoContentsPublisher), c =>
        {
            c.BaseAddress = new Uri(cfg.ForgejoBaseUrl);
            c.Timeout = timeout;
        });
        services.AddSingleton<IGitPublisher>(sp => new ForgejoContentsPublisher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ForgejoContentsPublisher)),
            sp.GetRequiredService<IBearerProvider>(),
            sp.GetRequiredService<EnrichmentConfig>(),
            repoRoot,
            sp.GetService<ILogger<ForgejoContentsPublisher>>()));

        // ── the three orchestration input seams (L-SEAMS) — live adapters ─────────────────────────
        // IAudioSource: the transient recording audio from the CT staging blob the Watcher writes
        // (content-addressed by audio_sha256). Read-only, never git-side; a missing blob → terminal.
        var audioStagingDir = Environment.GetEnvironmentVariable("CERVELLO_AUDIO_STAGING_DIR")
                              ?? "/var/lib/cervello/staging";
        services.AddSingleton<IAudioSource>(_ => new StagingBlobAudioSource(audioStagingDir));

        // IBaseTranscriptSource: the RATIFIED base — the recording's paired Google .txt transcript
        // read from the SAME CT staging blob store (content-addressed by the .txt's sha, carried on
        // RecordingRef.GoogleTxtSha256). Absent/unreadable → null (graceful degrade), never a throw.
        services.AddSingleton<IBaseTranscriptSource>(sp =>
            new StagingBlobTranscriptSource(audioStagingDir, sp.GetService<ILogger<StagingBlobTranscriptSource>>()));

        // IPriorSource: deterministic filename + manifest-participants prior, grounded against
        // map/people/<slug>.md (never invents a person). No network, no LLM.
        services.AddSingleton<IPriorSource>(_ => new ManifestPriorSource(repoRoot));

        // (IRecordingFactSource is the typed brain-api HttpClient registered above.)

        // Pin store: the on-CT blob write + sha are live; the external fetch is the ONE remaining L2
        // seam (a deploy registers the live IExternalBlobFetcher over the CT121 agentgateway). NOT
        // registered here or stub-faked (a fake would silently pin garbage) — so a full-live graph
        // resolves only once the deploy supplies it. This is the sole missing port the L-SEAMS
        // composition-completeness gate surfaces by name (CompositionCompletenessTests).
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

        // ── open-points MCP service (S50 L3 exposure) ───────────────────────────────────────────
        // The engine behind cervello_open_points_list / _answer (the operator's ONLY enrichment UI,
        // Claude web/mobile via the bridge, Surface A). Constructed here for the LIVE host: every
        // ctor dep — auth gate (273), IOpenPointStore (212), IAccessLog (238), CervelloGraphWriter
        // (276), ICorrectionMapStore (211), VoiceprintEnrollment (below), EnrollmentAllowlist (61),
        // IEnrollmentSourceProvider (269) — is a live registration. VoiceprintEnrollment wraps the
        // live IVoiceprintStore (210); it was previously only test-constructed. The service opens no
        // NATS/HTTP itself — the CT146 host maps a token-gated HTTP surface over it (Host/Program.cs);
        // the bearer gate FAILS CLOSED on an empty token (a mis-provisioned deploy exposes nothing).
        services.AddSingleton<VoiceprintEnrollment>();
        services.AddSingleton<OpenPointsService>();
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

        // No live git egress in fake mode: the searchable-substrate publisher is a no-op (the offline
        // slice writes artifacts to the in-memory stores; nothing is pushed to ste/cervello).
        services.AddSingleton<IGitPublisher, NoOpGitPublisher>();

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
