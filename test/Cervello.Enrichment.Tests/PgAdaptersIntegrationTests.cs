using System.Diagnostics;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// OPT-IN, offline pgvector integration tests for the LIVE Pg adapters (E3/E4/E5 deferred adapters).
/// SKIPPED BY DEFAULT: they only run when <c>CERVELLO_PGVECTOR_IT=1</c> is set AND a container runtime
/// (podman/docker) with a cached <c>pgvector/pgvector</c> image is available. So the default suite
/// stays fully offline + deterministic (no daemon, no network), while a host that has the cached
/// image (or L2 on-CT) can exercise the REAL DDL + cosine SQL + deletion-tombstone + refine behaviour
/// against a throwaway pgvector instance.
///
/// <para>This is the "run the Pg adapters' SQL against a pgvector testcontainer IF it runs fully
/// offline" path the mission asks for. When it does NOT run, the Pg adapters' SQL/migrations are
/// asserted by review (this file documents the exercised behaviour) + LIVE verification is deferred
/// to L2 against the real CT146 DB — see the mission return's L2 checklist.</para>
/// </summary>
[Trait("Category", "PgIntegration")]
public sealed class PgAdaptersIntegrationTests : IAsyncLifetime
{
    private const string Image = "docker.io/pgvector/pgvector:pg16";
    private const string ContainerName = "cervello-l1-pgvector-it";
    private string? _runtime;
    private int _port;
    private bool _enabled;
    private EnrichmentConfig _cfg = null!;

    public async Task InitializeAsync()
    {
        _enabled = Environment.GetEnvironmentVariable("CERVELLO_PGVECTOR_IT") == "1"
                   && (Which("podman") is { } p ? (_runtime = p) is not null
                       : Which("docker") is { } d && (_runtime = d) is not null);
        if (!_enabled) return;

        _port = 55400 + Random.Shared.Next(0, 90);
        // Best-effort clean any leftover, then start a throwaway pgvector.
        Run(_runtime!, $"rm -f {ContainerName}", ignoreFail: true);
        var start = Run(_runtime!,
            $"run --rm -d --name {ContainerName} -e POSTGRES_PASSWORD=pw -e POSTGRES_DB=cervello -p {_port}:5432 {Image}");
        if (start.ExitCode != 0) { _enabled = false; return; }

        _cfg = EnrichmentConfig.From(new Dictionary<string, string?>
        {
            ["CERVELLO_ENRICHMENT_DB_DSN"] =
                $"Host=127.0.0.1;Port={_port};Database=cervello;Username=postgres;Password=pw;SslMode=Disable",
        });

        // Wait for readiness (schema-ensure retries internally, but give the server a moment).
        await WaitReadyAsync();
    }

    public Task DisposeAsync()
    {
        if (_enabled && _runtime is not null) Run(_runtime, $"rm -f {ContainerName}", ignoreFail: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Voiceprint_store_cosine_matches_and_refine_and_deletion_tombstone()
    {
        if (!_enabled) return; // opt-in: set CERVELLO_PGVECTOR_IT=1 with a cached pgvector image

        var allow = new EnrollmentAllowlist(["guilhem"]);
        var store = new PgVoiceprintStore(_cfg, allow);
        await store.EnsureSchemaAsync();

        var v0 = TestVectors.Axis(0);
        await store.EnrollOrRefineAsync("guilhem", v0, ["rec://r#s1"], 0.7, new DateOnly(2026, 7, 1));

        // Match: an identical centroid is cosine ~1.
        var matches = await store.MatchAsync(v0);
        Assert.Equal("guilhem", matches[0].PersonSlug);
        Assert.True(matches[0].Cosine > 0.99);

        // Refine: a second confirmation increments sample_count to 2.
        var refined = await store.EnrollOrRefineAsync("guilhem", v0, ["rec://r#s2"], 0.7, new DateOnly(2026, 7, 2));
        Assert.Equal(2, refined.SampleCount);

        // Deletion runbook: centroid gone, tombstoned (R8: no re-attribution).
        Assert.True(await store.DeleteAsync("guilhem"));
        Assert.True(await store.IsDeletedAsync("guilhem"));
        Assert.Empty(await store.MatchAsync(v0));

        // §10 allowlist hard-gate: enrolling a non-allowlisted person throws.
        await Assert.ThrowsAsync<EnrollmentNotAllowedException>(() =>
            store.EnrollOrRefineAsync("stranger", v0, ["rec://r#s3"], null, new DateOnly(2026, 7, 3)));
    }

    [Fact]
    public async Task Open_point_store_enqueue_is_idempotent_and_resolve_is_single_shot()
    {
        if (!_enabled) return;

        var store = new PgOpenPointStore(_cfg);
        await store.EnsureSchemaAsync();

        var pt = new OpenPoint("op-1", OpenPointKind.Speaker, "rec-1", "bnd-1", "who is s1?",
            new[] { "guilhem" }, mergedSpeaker: "s1");
        Assert.True(await store.EnqueueAsync(pt));
        Assert.False(await store.EnqueueAsync(pt));                 // idempotent on point id
        Assert.Single(await store.ListPendingAsync("rec-1"));

        Assert.True(await store.ResolveAsync("op-1", OpenPointResolution.Answered("guilhem", "human://op-1", "op-1")));
        Assert.False(await store.ResolveAsync("op-1", OpenPointResolution.Answered("guilhem", "human://op-1", "op-1"))); // single-shot
        Assert.True(await store.IsResolvedAsync("op-1"));
        Assert.Empty(await store.ListPendingAsync("rec-1"));
    }

    [Fact]
    public async Task Correction_map_and_ledger_round_trip()
    {
        if (!_enabled) return;

        var map = new PgCorrectionMapStore(_cfg);
        await map.EnsureSchemaAsync();
        await map.UpsertAsync(new GlossaryEntry("Total Energies", "TotalEnergies", CorrectionKind.Term, "human://a1"));
        var glossary = await map.GetGlossaryAsync();
        Assert.Contains(glossary, g => g.Before == "Total Energies" && g.After == "TotalEnergies");

        var ledger = new PgEnrichmentLedger(_cfg);
        await ledger.EnsureSchemaAsync();
        Assert.True(await ledger.TryClaimAsync("rec:rec-1:sha"));
        Assert.False(await ledger.TryClaimAsync("rec:rec-1:sha"));  // claim-once, replay no-op
        Assert.True(await ledger.IsClaimedAsync("rec:rec-1:sha"));
    }

    /// <summary>
    /// MIGRATE-FIX regression: ensuring EVERY Pg adapter's schema (as the host startup loop does over
    /// all ISchemaInitializers) creates ALL expected tables — not just enrichment_ledger. This is the
    /// exact failure the mission fixes: a fresh CT146 that ensured only the ledger had no
    /// correction_map, so PgCorrectionMapStore.GetGlossaryAsync threw 42P01. Here we ensure each Pg
    /// store's schema against a fresh pgvector DB and assert every table now exists.
    /// </summary>
    [Fact]
    public async Task Ensuring_all_pg_adapter_schemas_creates_every_expected_table()
    {
        if (!_enabled) return; // opt-in: set CERVELLO_PGVECTOR_IT=1 with a cached pgvector image

        var allow = new EnrollmentAllowlist(["guilhem"]);
        // Each store is an ISchemaInitializer; the host startup loop ensures all of them.
        var initializers = new ISchemaInitializer[]
        {
            new PgEnrichmentLedger(_cfg),
            new PgCorrectionMapStore(_cfg),
            new PgVoiceprintStore(_cfg, allow),
            new PgOpenPointStore(_cfg),
        };
        foreach (var init in initializers)
            await init.EnsureSchemaAsync();

        foreach (var table in new[]
                 {
                     "enrichment_ledger", "correction_map",
                     "voiceprints", "voiceprint_tombstones", "voiceprint_enrollment_audio",
                     "open_points",
                 })
        {
            Assert.True(await TableExistsAsync(table), $"expected table '{table}' to exist after ensuring all schemas");
        }
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var c = new Npgsql.NpgsqlConnection(_cfg.PostgresDsn);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT to_regclass(@t) IS NOT NULL", c);
        cmd.Parameters.AddWithValue("t", $"public.{table}");
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task WaitReadyAsync()
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                await using var c = new Npgsql.NpgsqlConnection(_cfg.PostgresDsn);
                await c.OpenAsync();
                return;
            }
            catch { await Task.Delay(500); }
        }
    }

    private static string? Which(string tool)
    {
        var r = Run("/usr/bin/env", $"which {tool}", ignoreFail: true);
        var path = r.StdOut.Trim();
        return r.ExitCode == 0 && path.Length > 0 ? path : null;
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string file, string args, bool ignoreFail = false)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            return (p.ExitCode, so, se);
        }
        catch when (ignoreFail) { return (-1, "", ""); }
    }
}
