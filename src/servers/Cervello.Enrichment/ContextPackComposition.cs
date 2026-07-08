using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cervello.Enrichment;

/// <summary>
/// DI composition for the DIALOGUE-INTERACTION backend (design §5): the context-pack assembler +
/// map-read + capture/goal services the new bridge tools call. Kept SEPARATE from
/// <see cref="EnrichmentComposition"/> (the recording-enrichment engine) so the two deploy slices are
/// independent — but both live on the same CT146 host binary + reuse the same live-vs-fake flag
/// (<see cref="EnrichmentConfig.UseLiveAdapters"/>) so switching is configuration, never code.
///
/// <para>Ports wired here: <see cref="IIndexerSearch"/> (hybrid search reuse, §2.2/§5.2),
/// <see cref="IMapGraph"/> (map read + traversal, §2/§5.3/§5.4), <see cref="IDeltaCursorStore"/>
/// (per-caller delta baseline, §2.6/Q9), <see cref="IDepositStore"/> (capture deposit, §5.5). It
/// REUSES the engine's already-wired ports where they overlap — <see cref="IOpenPointStore"/> (the
/// pack's open-points piggyback, §2.1) and <see cref="CervelloGraphWriter"/> (the goal/evidence
/// review-PR, §5.6/§5.7) — so this call MUST run after <see cref="EnrichmentComposition.AddCervelloEnrichment"/>.</para>
///
/// <para><b>Live-only migrations.</b> The Pg delta-cursor store is registered as an
/// <see cref="ISchemaInitializer"/> so the host ensures its table on startup alongside the engine's
/// tables (fail-closed). In fake mode no schema initializer is registered (in-memory stores).</para>
/// </summary>
public static class ContextPackComposition
{
    /// <summary>Wire the context-pack + map-read + capture/goal backend (design §5). Call AFTER AddCervelloEnrichment.</summary>
    public static IServiceCollection AddCervelloContextPack(this IServiceCollection services, EnrichmentConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cfg);

        if (cfg.UseLiveAdapters)
            AddLive(services, cfg);
        else
            AddFake(services);

        // The three services compose over the ports above. ContextPackAssembler also consumes the
        // engine-wired IOpenPointStore (piggyback); GoalService consumes the engine-wired
        // CervelloGraphWriter (review-PR). Both are already registered by AddCervelloEnrichment.
        services.AddSingleton<ContextPackAssembler>();
        services.AddSingleton<CaptureService>();
        services.AddSingleton<GoalService>();
        return services;
    }

    private static void AddLive(IServiceCollection services, EnrichmentConfig cfg)
    {
        var repoRoot = Environment.GetEnvironmentVariable("CERVELLO_REPO_WORKTREE") ?? "/var/lib/cervello/repo";
        var timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds);

        // Indexer hybrid search (:8009) — a named, pooled HttpClient + the static bearer bound at
        // construction (the token is a ctor arg, so we build the client explicitly rather than using
        // AddHttpClient<TInterface> which cannot pass it).
        services.AddHttpClient("cervello-indexer", c =>
        {
            c.BaseAddress = new Uri(cfg.IndexerBaseUrl);
            c.Timeout = timeout;
        });
        services.AddSingleton<IIndexerSearch>(sp =>
            new IndexerSearchClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("cervello-indexer"),
                cfg.IndexerSearchToken,
                sp.GetService<ILogger<IndexerSearchClient>>()));

        // Map graph — the CT-local working tree (verbatim reads; no LLM).
        services.AddSingleton<IMapGraph>(_ => new RepoMapGraph(repoRoot));

        // Deposit store — capture candidates into conversations/ + inbox/ (never map/).
        services.AddSingleton<IDepositStore>(_ => new RepoDepositStore(repoRoot));

        // Delta cursor — per-caller server-side baseline (CT146 Postgres); ensure its table on startup.
        services.AddSingleton<PgDeltaCursorStore>();
        services.AddSingleton<IDeltaCursorStore>(sp => sp.GetRequiredService<PgDeltaCursorStore>());
        services.AddSingleton<ISchemaInitializer>(sp => sp.GetRequiredService<PgDeltaCursorStore>());
    }

    private static void AddFake(IServiceCollection services)
    {
        // In fake mode the indexer/graph/deposit have no in-engine live default — a fake-mode host
        // wires its own (the test harness does). We register only the offline-safe stores so a
        // fake-mode graph resolves without a DB/network for the parts that have an in-engine fake.
        services.AddSingleton<IDeltaCursorStore, InMemoryDeltaCursorStore>();
        services.AddSingleton<IDepositStore, InMemoryDepositStore>();
    }
}
