using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Host.Drain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cervello.Enrichment.Host.Tests;

/// <summary>
/// The host's own DI wiring: it registers the pipeline entry stage + the drain source, and the
/// drain source tracks the engine's live-vs-fake flag (fake mode stays fully offline — no DB). The
/// engine's composition (stores, ledger, phase gate) is covered by the engine suite; here we assert
/// the HOST-added registrations resolve.
/// </summary>
public sealed class HostCompositionTests
{
    private static IServiceProvider Build(bool live)
    {
        var env = new Dictionary<string, string?> { ["CERVELLO_USE_LIVE_ADAPTERS"] = live ? "true" : "false" };
        if (live)
        {
            env["CERVELLO_DB_PASSWORD"] = "unused-at-construction";
            Environment.SetEnvironmentVariable("OIDC_ISSUER", "https://id.test");
            Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", "proj-1");
        }
        var engineCfg = EnrichmentConfig.From(env);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(HostConfig.From(new Dictionary<string, string?>()));
        services.AddCervelloEnrichment(engineCfg);
        services.AddSingleton<IngestStage>();
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
        }
        services.AddSingleton<DrainWorker>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Fake_mode_wires_the_in_memory_drain_source_offline()
    {
        var sp = Build(live: false);
        Assert.IsType<InMemoryNormalizedWorkQueue>(sp.GetRequiredService<INormalizedWorkQueue>());
        Assert.NotNull(sp.GetRequiredService<IngestStage>());
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
}
