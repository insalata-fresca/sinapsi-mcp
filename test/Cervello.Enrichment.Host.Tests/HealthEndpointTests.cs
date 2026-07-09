using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Host.Drain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cervello.Enrichment.Host.Tests;

/// <summary>
/// The opaque health heartbeat (mirror the Watcher's /healthz): 200 <c>{"status":"ok"}</c> once the
/// drain worker is up, 503 while starting. Carries NO recording data — the only thing that leaves the
/// CT (invariant 3). Uses the in-memory TestServer (no real socket, no DB, no network).
/// </summary>
public sealed class HealthEndpointTests
{
    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // Wire the health dependency + its (fake) deps — the SAME /healthz shape as Program.cs. The
        // worker now runs the FULL pipeline, so we build it from a minimal set of fakes; with an empty
        // queue the health test never invokes a stage — it only asserts the worker reaches Ready.
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        builder.Services.AddSingleton(HostConfig.From(new Dictionary<string, string?>()));
        builder.Services.AddSingleton<INormalizedWorkQueue>(queue);
        builder.Services.AddSingleton<IEnrichmentLedger>(ledger);
        builder.Services.AddSingleton(BuildPipeline(ledger));
        builder.Services.AddSingleton<DrainWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DrainWorker>());

        var app = builder.Build();
        app.MapGet("/healthz", (DrainWorker w) =>
            w.Ready
                ? Results.Json(new { status = "ok" }, statusCode: 200)
                : Results.Json(new { status = "starting" }, statusCode: 503));
        return app;
    }

    /// <summary>Build a full pipeline over the given ledger from minimal host-local fakes.</summary>
    private static EnrichmentPipeline BuildPipeline(IEnrichmentLedger ledger) =>
        new(
            new IngestStage(ledger),
            new HostFakeAudioSource(),
            new BaseTranscribeStage(new HostFakeBaseTranscriptSource(), new HostInMemoryTranscriptStore()),
            new DiarizeEmbedStage(HostFakeDiarizeEmbedClient.Empty()),
            new ClusterMergeStage(),
            new AttributionStage(
                new InMemoryVoiceprintStore(EnrollmentAllowlist.Empty),
                new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly)),
            new CorrectionStage(new HostFakeCorrectionLlm(), new InMemoryCorrectionMapStore(),
                new HostFakeReAsrClient(), new CorrectionGrader(PolicyPhase.EscalateOnly)),
            new EnrichLinkStage(new HostFakeLinkResolver()),
            new ApplyStage(
                new CervelloGraphWriter(new HostFakeMapPrWriter(), new HostFakeLinkResolver(), new HostFakePinStore()),
                new InMemoryOpenPointStore()),
            new HostFakeRecordingFactSource());

    [Fact]
    public async Task Healthz_is_503_before_start_and_200_once_the_worker_is_ready()
    {
        var app = BuildApp();
        var worker = app.Services.GetRequiredService<DrainWorker>();

        // Before the hosted service runs, the worker is not ready → 503.
        Assert.False(worker.Ready);

        await app.StartAsync();
        try
        {
            var client = app.GetTestClient();

            // Poll briefly: ExecuteAsync sets Ready almost immediately after start.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            HttpResponseMessage resp;
            do
            {
                resp = await client.GetAsync("/healthz");
                if (resp.StatusCode == System.Net.HttpStatusCode.OK) break;
                await Task.Delay(25);
            } while (DateTime.UtcNow < deadline);

            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("ok", body);
            // Opaque — no recording data in the body.
            Assert.DoesNotContain("rec:", body);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
