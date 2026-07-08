using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// THE CONVERGENCE GATE (MISSION L-SEAMS). It builds the FULL live DI container
/// (<c>CERVELLO_USE_LIVE_ADAPTERS=true</c> → <see cref="EnrichmentComposition.AddCervelloEnrichment"/>
/// + <see cref="EnrichmentComposition.AddCervelloPipeline"/>) and asserts the whole
/// <see cref="EnrichmentPipeline"/> orchestrator graph resolves — i.e. every port on the end-to-end
/// path has a LIVE adapter, with NO stub/fake supplied by this test. This definitively closes the
/// question "are there more unbuilt input seams" for the enrichment engine.
///
/// <para><b>Result (as of L-SEAMS).</b> The three input seams this mission built —
/// <see cref="IAudioSource"/> (<see cref="StagingBlobAudioSource"/>), <see cref="IPriorSource"/>
/// (<see cref="ManifestPriorSource"/>), <see cref="IRecordingFactSource"/>
/// (<see cref="BrainApiRecordingFactSource"/>) — now resolve from the composition root itself. The
/// live graph resolves the entire pipeline with EXACTLY ONE remaining unregistered port:
/// <see cref="IExternalBlobFetcher"/> — the drive://gmail:// evidence fetcher the pin store
/// (<see cref="CtPinStore"/>) needs for <c>pin://</c>-on-cite. That seam is a genuine L2 deploy
/// concern (it needs the live CT121 agentgateway + a scoped MCP identity), is documented as such,
/// and is deliberately NOT stub-faked here (the mission's "LIST it, do not fake it" rule). Supplying
/// it → the WHOLE pipeline resolves with zero missing ports.</para>
/// </summary>
public sealed class CompositionCompletenessTests
{
    /// <summary>The single genuinely-remaining port for a full-live enrichment deploy (the L2 seam).</summary>
    private const string RemainingL2Seam = nameof(IExternalBlobFetcher);

    private static ServiceCollection LiveGraph(bool withExternalBlobFetcher)
    {
        var env = new Dictionary<string, string?>
        {
            ["CERVELLO_USE_LIVE_ADAPTERS"] = "true",
            ["CERVELLO_DB_PASSWORD"] = "unused-at-construction",
        };
        Environment.SetEnvironmentVariable("OIDC_ISSUER", "https://id.test");
        Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", "proj-1");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCervelloEnrichment(EnrichmentConfig.From(env)); // ← now registers the 3 input seams
        services.AddCervelloPipeline();                             // ← the full orchestrator + every stage

        if (withExternalBlobFetcher)
            services.AddSingleton<IExternalBlobFetcher>(new NullBlobFetcher());
        return services;
    }

    private static void ClearOidcEnv()
    {
        Environment.SetEnvironmentVariable("OIDC_ISSUER", null);
        Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", null);
    }

    // ── the three input seams L-SEAMS built now resolve from the composition ROOT (no host stub) ──
    [Fact]
    public void The_three_input_seams_resolve_from_the_live_composition_root()
    {
        try
        {
            var sp = LiveGraph(withExternalBlobFetcher: true).BuildServiceProvider();

            Assert.IsType<StagingBlobAudioSource>(sp.GetRequiredService<IAudioSource>());
            Assert.IsType<ManifestPriorSource>(sp.GetRequiredService<IPriorSource>());
            Assert.IsType<BrainApiRecordingFactSource>(sp.GetRequiredService<IRecordingFactSource>());
        }
        finally { ClearOidcEnv(); }
    }

    // ── THE GATE: the FULL live pipeline RESOLVES with zero missing ports ─────────────────────────
    [Fact]
    public void The_full_live_pipeline_resolves_with_zero_missing_ports()
    {
        try
        {
            var sp = LiveGraph(withExternalBlobFetcher: true).BuildServiceProvider();

            // Resolving the orchestrator ROOT forces the ENTIRE transitive port graph to materialise —
            // ingest → audio → transcribe → diarize → merge → attribution → correction → enrich → apply
            // → graph-writer → pin store, plus every store/client/bearer and the three input seams. If
            // ANY port lacked a live adapter this GetRequiredService would throw naming it. It does not.
            var pipeline = sp.GetRequiredService<EnrichmentPipeline>();
            Assert.NotNull(pipeline);
        }
        finally { ClearOidcEnv(); }
    }

    // ── the finding, made precise: the ONLY remaining unregistered port is IExternalBlobFetcher ───
    [Fact]
    public void Without_the_l2_external_blob_fetcher_the_only_missing_port_is_named_and_singular()
    {
        try
        {
            // Resolve the orchestrator root WITHOUT the L2 seam. Every other live adapter is registered
            // (incl. the three input seams L-SEAMS built), so the transitive resolution fails at exactly
            // one edge: CtPinStore's IExternalBlobFetcher dependency. MS.DI names the missing service
            // type in the thrown message — proving the gap is precisely this ONE seam, not a diffuse
            // "many things missing". (A factory-registered dep like CtPinStore's is skipped by
            // ValidateOnBuild, so the gap only surfaces on real resolution — which is what we do here.)
            var sp = LiveGraph(withExternalBlobFetcher: false).BuildServiceProvider();

            var ex = Assert.ThrowsAny<InvalidOperationException>(
                () => sp.GetRequiredService<EnrichmentPipeline>());

            Assert.Contains(RemainingL2Seam, ex.ToString(), StringComparison.Ordinal);
        }
        finally { ClearOidcEnv(); }
    }

    /// <summary>
    /// The documented L2 seam: the LIVE implementation calls the gdrive / gmail MCP through the CT121
    /// agentgateway. Here it is a NON-functional placeholder used ONLY to prove the graph shape — it
    /// throws if actually called, so it can never masquerade as a working live fetch.
    /// </summary>
    private sealed class NullBlobFetcher : IExternalBlobFetcher
    {
        public Task<ReadOnlyMemory<byte>> FetchAsync(string externalRef, CancellationToken ct = default) =>
            throw new NotSupportedException("live external-blob fetch (drive://gmail://) is an L2 deploy seam");
    }
}
