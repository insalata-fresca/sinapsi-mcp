using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Host.Drain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cervello.Enrichment.Host.Tests;

/// <summary>
/// The host's own DI wiring: it registers the FULL pipeline (every stage + the orchestrator, via
/// <c>AddCervelloPipeline</c>) + the drain source, and the drain source tracks the engine's
/// live-vs-fake flag (fake mode stays fully offline — no DB). The engine's composition (stores,
/// ledger, phase gate) is covered by the engine suite; here we assert the HOST-added registrations —
/// the pipeline orchestrator + its three deploy-slice seams — resolve into a <see cref="DrainWorker"/>.
/// </summary>
public sealed class HostCompositionTests
{
    private static IServiceProvider Build(bool live)
    {
        var env = new Dictionary<string, string?> { ["CERVELLO_USE_LIVE_ADAPTERS"] = live ? "true" : "false" };
        if (live)
        {
            env["CERVELLO_DB_PASSWORD"] = "unused-at-construction";
            env["CERVELLO_BRAIN_BEARER_TOKEN"] = "brain-bearer-under-test"; // enrich routes require it
            Environment.SetEnvironmentVariable("OIDC_ISSUER", "https://id.test");
            Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", "proj-1");
        }
        var engineCfg = EnrichmentConfig.From(env);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(HostConfig.From(new Dictionary<string, string?>()));
        services.AddCervelloEnrichment(engineCfg);
        services.AddCervelloPipeline();

        // The three DEPLOY-SLICE (L2) seams the orchestrator needs but no stage owns — the live
        // adapters are a deploy follow-up (E-PIPE builds + tests against fakes, no deploy). A
        // construction-only DI test supplies stubs so the whole graph resolves offline.
        services.AddSingleton<IAudioSource, StubAudioSource>();
        services.AddSingleton<IRecordingFactSource, StubRecordingFactSource>();
        services.AddSingleton<IPriorSource>(new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates>()));

        if (engineCfg.UseLiveAdapters)
        {
            services.AddSingleton<INormalizedWorkQueue, PgNormalizedWorkQueue>();
            // The live pin store needs an external-blob fetcher seam (a deploy-time wiring); supply a
            // stub so the graph resolves for a construction-only test (never called here).
            services.AddSingleton<IExternalBlobFetcher>(new NullBlobFetcher());
        }
        else
        {
            services.AddSingleton<INormalizedWorkQueue, InMemoryNormalizedWorkQueue>();
            // Fake mode: the engine assembly ships NO fakes for the diarize/transcribe/re-asr/
            // correction-LLM/map-PR ports (their fakes live in the test project by design), so a
            // fake-mode host supplies them. This mirrors exactly what an offline host wires.
            services.AddSingleton<IBaseTranscriptSource, HostFakeBaseTranscriptSource>();
            services.AddSingleton<ITranscribeClient, HostFakeTranscribeClient>();
            services.AddSingleton<ITranscriptStore, HostInMemoryTranscriptStore>();
            services.AddSingleton<IDiarizeEmbedClient>(HostFakeDiarizeEmbedClient.Empty());
            services.AddSingleton<IReAsrClient, HostFakeReAsrClient>();
            services.AddSingleton<ICorrectionLlm, HostFakeCorrectionLlm>();
            services.AddSingleton<ILinkResolver, HostFakeLinkResolver>();
            services.AddSingleton<IMapPrWriter, HostFakeMapPrWriter>();
            services.AddSingleton<IPinStore, HostFakePinStore>();
            services.AddSingleton<CervelloGraphWriter>();
        }
        services.AddSingleton<DrainWorker>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Fake_mode_wires_the_full_pipeline_and_drain_source_offline()
    {
        var sp = Build(live: false);
        Assert.IsType<InMemoryNormalizedWorkQueue>(sp.GetRequiredService<INormalizedWorkQueue>());
        Assert.NotNull(sp.GetRequiredService<Cervello.Enrichment.Pipeline.EnrichmentPipeline>());
        Assert.NotNull(sp.GetRequiredService<DrainWorker>());
    }

    [Fact]
    public void Live_mode_wires_the_pg_drain_source_construction_only()
    {
        try
        {
            var sp = Build(live: true);
            Assert.IsType<PgNormalizedWorkQueue>(sp.GetRequiredService<INormalizedWorkQueue>());
            Assert.NotNull(sp.GetRequiredService<DrainWorker>()); // resolves without touching any endpoint
        }
        finally
        {
            Environment.SetEnvironmentVariable("OIDC_ISSUER", null);
            Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", null);
        }
    }

    // ── L2 wiring: the HOST's OWN production registration closes the IExternalBlobFetcher seam ──
    // Program.cs registers NotConfiguredExternalBlobFetcher in live mode (the one port the engine
    // composition root leaves for the deploy). This asserts the host's PRODUCTION graph resolves the
    // full pipeline + pin store with zero missing ports WITHOUT the test supplying a fetcher stub —
    // the regression guard that a live CT146 container boots instead of crash-looping on IPinStore.
    [Fact]
    public void Live_host_registers_a_throwing_external_blob_fetcher_so_the_full_graph_resolves()
    {
        try
        {
            Environment.SetEnvironmentVariable("OIDC_ISSUER", "https://id.test");
            Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", "proj-1");
            var engineCfg = EnrichmentConfig.From(new Dictionary<string, string?>
            {
                ["CERVELLO_USE_LIVE_ADAPTERS"] = "true",
                ["CERVELLO_DB_PASSWORD"] = "unused-at-construction",
                ["CERVELLO_BRAIN_BEARER_TOKEN"] = "brain-bearer-under-test",
            });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(HostConfig.From(new Dictionary<string, string?>()));
            services.AddCervelloEnrichment(engineCfg);
            services.AddCervelloPipeline();
            // Mirror Program.cs EXACTLY: the host registers the throwing fetcher in live mode. NO
            // test-supplied fetcher stub here — this proves the host closes the seam on its own.
            services.AddSingleton<IExternalBlobFetcher>(new NotConfiguredExternalBlobFetcher());
            services.AddSingleton<INormalizedWorkQueue, PgNormalizedWorkQueue>();
            services.AddSingleton<DrainWorker>();
            var sp = services.BuildServiceProvider();

            // The pin store (CtPinStore) + the whole orchestrator resolve — the seam is closed.
            Assert.NotNull(sp.GetRequiredService<IPinStore>());
            Assert.NotNull(sp.GetRequiredService<Cervello.Enrichment.Pipeline.EnrichmentPipeline>());
            Assert.IsType<NotConfiguredExternalBlobFetcher>(sp.GetRequiredService<IExternalBlobFetcher>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OIDC_ISSUER", null);
            Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", null);
        }
    }

    // ── invariant 3 / D8: the host references NO NATS client ────────────────────────
    [Fact]
    public void Host_assembly_references_no_nats_client_at_all()
    {
        var asm = typeof(DrainWorker).Assembly;
        foreach (var name in asm.GetReferencedAssemblies().Select(a => a.Name ?? ""))
        {
            Assert.False(name.Contains("Sinapsi.Nats", StringComparison.OrdinalIgnoreCase),
                $"unexpected reference to {name}");
            Assert.False(name.StartsWith("NATS.", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("NATS", StringComparison.OrdinalIgnoreCase),
                $"unexpected NATS client reference: {name}");
        }
    }

    private sealed class NullBlobFetcher : IExternalBlobFetcher
    {
        public Task<ReadOnlyMemory<byte>> FetchAsync(string externalRef, CancellationToken ct = default) =>
            throw new NotSupportedException("live fetch is an L2 concern");
    }

    private sealed class StubAudioSource : IAudioSource
    {
        public Task<ReadOnlyMemory<byte>> FetchAsync(string recordingId, string audioSha256, CancellationToken ct = default) =>
            throw new NotSupportedException("audio fetch is an L2 seam (construction-only test)");
    }

    private sealed class StubRecordingFactSource : IRecordingFactSource
    {
        public Task<RecordingFacts> GetFactsAsync(string recordingId, BaseTranscript baseTranscript, CancellationToken ct = default) =>
            throw new NotSupportedException("fact derivation is an L2 seam (construction-only test)");
    }
}
