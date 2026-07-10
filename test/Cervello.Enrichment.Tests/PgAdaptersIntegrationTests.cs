using System.Diagnostics;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;
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

    /// <summary>
    /// M3 corpus store (design <c>ste/cervello</c> <c>docs/design/autonomous-attribution.md</c>
    /// §4.1/§5): the real DDL + cosine-cast SQL round-trips a recording's centroids, upserts
    /// idempotently on <c>(recording_id, cluster_index)</c> — NEVER the diarizer's <c>s1…</c> label —
    /// and a corpus-wide query returns rows across multiple recordings coexisting. Distinct from
    /// <c>voiceprints</c> (the confirmed enrolled-person table), proven untouched by this same run
    /// (below).
    /// </summary>
    [Fact]
    public async Task Recording_voiceprint_store_round_trips_upserts_idempotently_and_queries_the_corpus()
    {
        if (!_enabled) return; // opt-in: set CERVELLO_PGVECTOR_IT=1 with a cached pgvector image

        var store = new PgRecordingVoiceprintStore(_cfg);
        await store.EnsureSchemaAsync();

        // Seed the CONFIRMED enrolled-person store too (its own table), so the "untouched" assertion
        // below proves the M3 corpus store never mutates it — not merely that the table is absent.
        var enrolledStore = new PgVoiceprintStore(_cfg, new EnrollmentAllowlist(["guilhem"]));
        await enrolledStore.EnsureSchemaAsync();
        await enrolledStore.EnrollOrRefineAsync(
            "guilhem", TestVectors.Axis(0), ["rec://seed#s1"], null, new DateOnly(2026, 7, 1));

        var v0 = TestVectors.Axis(0);
        var v1 = TestVectors.Axis(10);

        // V0 (design ste/cervello docs/design/voiceprint-naming.md §1.1/§5): rec-1's first cluster
        // carries two segment ranges — proves the real DDL/SQL round-trips them, not just the InMemory
        // contract test.
        var rec1Seg0 = new DiarizedSegment[] { new("s1", 0.0, 5.0), new("s1", 12.0, 30.5) };

        // Round-trip: persist rec-1's two merged clusters, fetch them back verbatim, ordered by index.
        await store.PersistAsync("rec-1",
        [
            new RecordingVoiceprint("rec-1", 0, v0, "pyannote/wespeaker-voxceleb-resnet34-LM", 4, 20.0, "s1", DateTimeOffset.UtcNow, rec1Seg0),
            new RecordingVoiceprint("rec-1", 1, v1, "pyannote/wespeaker-voxceleb-resnet34-LM", 2, 8.5, "s3", DateTimeOffset.UtcNow),
        ]);
        var rec1 = await store.GetForRecordingAsync("rec-1");
        Assert.Equal(2, rec1.Count);
        Assert.Equal(0, rec1[0].ClusterIndex);
        Assert.Equal(1, rec1[1].ClusterIndex);
        Assert.True(Cosine.Similarity(rec1[0].Centroid, v0) > 0.99); // pgvector round-trip preserves the vector
        Assert.Equal(2, rec1[0].Segments.Count);                      // segment ranges round-trip too
        Assert.Equal(0.0, rec1[0].Segments[0].Start);
        Assert.Equal(5.0, rec1[0].Segments[0].End);
        Assert.Equal(12.0, rec1[0].Segments[1].Start);
        Assert.Equal(30.5, rec1[0].Segments[1].End);
        Assert.Empty(rec1[1].Segments);                               // cluster 1 was persisted with none

        // GetSegmentsAsync — the dedicated per-cluster read the naming surface/clip-cutter will call.
        var seg0 = await store.GetSegmentsAsync("rec-1", 0);
        Assert.Equal(2, seg0.Count);
        Assert.Equal(5.0, seg0[0].End);

        // Idempotent upsert: re-persisting the SAME (recording_id, cluster_index) key updates in place,
        // never duplicates — even though the diarizer label changes (labels are never the identity).
        // The segment ranges also change (a re-run of diarize-embed need not reproduce the exact same
        // boundaries) — the wholesale delete-then-insert must leave exactly the NEW set, no stale rows.
        var refinedSeg0 = new DiarizedSegment[] { new("s1", 0.0, 6.0), new("s1", 10.0, 20.0), new("s1", 25.0, 40.0) };
        await store.PersistAsync("rec-1",
        [
            new RecordingVoiceprint("rec-1", 0, v0, "pyannote/wespeaker-voxceleb-resnet34-LM", 9, 45.0, "s1-relabelled", DateTimeOffset.UtcNow, refinedSeg0),
        ]);
        var rec1AfterUpsert = await store.GetForRecordingAsync("rec-1");
        Assert.Equal(2, rec1AfterUpsert.Count); // still 2 rows — upsert, not a duplicate
        Assert.Equal(9, rec1AfterUpsert.Single(r => r.ClusterIndex == 0).SegmentCount);
        var seg0AfterUpsert = await store.GetSegmentsAsync("rec-1", 0);
        Assert.Equal(3, seg0AfterUpsert.Count);   // reflects the LATEST persist, not a union with the old 2
        Assert.Equal(6.0, seg0AfterUpsert[0].End);

        // Corpus-wide query: a DIFFERENT recording coexists; both are returned together, each with its
        // OWN segments attached (no cross-recording/cross-cluster mixup in the batch attach).
        await store.PersistAsync("rec-2",
        [
            new RecordingVoiceprint("rec-2", 0, TestVectors.Axis(50), "pyannote/wespeaker-voxceleb-resnet34-LM", 1, 3.0, "s1",
                DateTimeOffset.UtcNow, [new DiarizedSegment("s1", 100.0, 103.0)]),
        ]);
        var corpus = await store.GetCorpusAsync();
        Assert.Equal(3, corpus.Count); // rec-1's 2 + rec-2's 1
        Assert.Contains(corpus, r => r.RecordingId == "rec-1" && r.ClusterIndex == 0);
        Assert.Contains(corpus, r => r.RecordingId == "rec-1" && r.ClusterIndex == 1);
        var rec2c0 = corpus.Single(r => r.RecordingId == "rec-2" && r.ClusterIndex == 0);
        Assert.Equal(100.0, rec2c0.Segments.Single().Start);

        // The confirmed enrolled-person table is untouched by this store (design invariant): still
        // exactly the ONE seeded row, unaffected by all the recording_voiceprints activity above.
        var enrolledCount = await ScalarCountAsync("voiceprints");
        Assert.Equal(1, enrolledCount);
        Assert.NotNull(await enrolledStore.GetAsync("guilhem"));
    }

    private async Task<long> ScalarCountAsync(string table)
    {
        await using var c = new Npgsql.NpgsqlConnection(_cfg.PostgresDsn);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT COUNT(*) FROM {table}", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
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
            new PgRecordingVoiceprintStore(_cfg),
        };
        foreach (var init in initializers)
            await init.EnsureSchemaAsync();

        foreach (var table in new[]
                 {
                     "enrichment_ledger", "correction_map", "recording_voiceprints",
                     "recording_voiceprint_segments",
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
