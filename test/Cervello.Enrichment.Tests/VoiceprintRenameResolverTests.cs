using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// V5 rename → enroll → move → re-attribute tests (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V5). Exercises: detect a rename by fileId→name,
/// enroll the EXACT centroid mapped to that file (never a different voice), the human:// basis, the §10
/// consent-by-rename add, move-to-registry, resolved-marking, only-unresolved-candidates, idempotent
/// retry on a mid-way failure, and the no-fabrication (empty-slug) skip. SYNTHETIC vectors only.
/// </summary>
public sealed class VoiceprintRenameResolverTests
{
    private static readonly DateOnly On = new(2026, 7, 10);

    // ── an in-memory Drive registry surface: files are {id → name}; move records the moved id ──────
    private sealed class FakeRegistryDrive : IVoiceprintRegistryDrive
    {
        private readonly Dictionary<string, string> _files; // fileId → current name
        public List<string> Moved { get; } = [];
        public bool FailMove { get; set; }
        public bool FailList { get; set; }

        public FakeRegistryDrive(Dictionary<string, string> files) => _files = files;

        public Task<IReadOnlyList<DriveFileEntry>> ListVoiceprintsFolderAsync(CancellationToken ct = default)
        {
            if (FailList) throw new InvalidOperationException("synthetic list failure (grant not widened)");
            return Task.FromResult<IReadOnlyList<DriveFileEntry>>(
                _files.Select(kv => new DriveFileEntry(kv.Key, kv.Value)).ToList());
        }

        public Task MoveToRegistryAsync(string fileId, CancellationToken ct = default)
        {
            if (FailMove) throw new InvalidOperationException("synthetic move failure");
            Moved.Add(fileId);
            _files.Remove(fileId); // the file leaves the voiceprints/ folder
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingAccessLog : IAccessLog
    {
        public List<AccessLogEntry> Entries { get; } = [];
        public Task AppendAsync(AccessLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static VoiceprintNamingCandidate Candidate(string fileId, string sampleName, int axis) =>
        new(sampleName, fileId, TestVectors.Axis(axis),
            [new VoiceReviewMember("rec-1", 0, 20.0, 1)], new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));

    private static (VoiceprintRenameResolver Resolver, InMemoryVoiceprintStore Store,
        InMemoryVoiceprintNamingCandidateStore Candidates, InMemoryEnrollmentConsentStore Consent,
        FakeRegistryDrive Drive, InMemoryRecentEnrollmentStore Recent, CapturingAccessLog Log)
        Build(Dictionary<string, string> driveFiles, CorpusReattributor? reattr = null)
    {
        var consent = new InMemoryEnrollmentConsentStore();
        var store = new InMemoryVoiceprintStore(EnrollmentAllowlist.Empty, consent);
        var enrollment = new VoiceprintEnrollment(store);
        var candidates = new InMemoryVoiceprintNamingCandidateStore();
        var drive = new FakeRegistryDrive(driveFiles);
        var recent = new InMemoryRecentEnrollmentStore();
        var log = new CapturingAccessLog();
        var resolver = new VoiceprintRenameResolver(drive, candidates, consent, enrollment, recent, reattr, log);
        return (resolver, store, candidates, consent, drive, recent, log);
    }

    [Fact] // scenario: a rename unknown_03 → Marco enrolls the EXACT centroid, adds consent, moves, resolves
    public async Task Rename_enrolls_exact_centroid_with_human_basis_adds_consent_and_moves()
    {
        var driveFiles = new Dictionary<string, string> { ["file-3"] = "Marco" };
        var (resolver, store, candidates, consent, drive, _, log) = Build(driveFiles);
        await candidates.ReplaceUnresolvedAsync([Candidate("file-3", "unknown_03", axis: 7)]);

        var result = await resolver.RunCycleAsync(On);

        Assert.Single(result.ResolvedSlugs);
        Assert.Equal("marco", result.ResolvedSlugs[0]);

        // Enrolled the EXACT centroid mapped to file-3 (axis 7), under slug "marco".
        var print = await store.GetAsync("marco");
        Assert.NotNull(print);
        Assert.Equal(TestVectors.Axis(7), print!.Centroid);

        // Consent-by-rename recorded (the §10 gate passed because of it — allowlist was Empty).
        Assert.True(await consent.IsConsentedAsync("marco"));

        // Moved to registry + marked resolved.
        Assert.Contains("file-3", drive.Moved);
        var row = await candidates.GetByDriveFileIdAsync("file-3");
        Assert.NotNull(row);
        Assert.True(row!.Resolved);

        // Access-logged with an enrolled + moved outcome.
        Assert.Contains(log.Entries, e => e.Tool == "voiceprint_rename_enroll" && e.Outcome.Contains("marco"));
    }

    [Fact] // scenario: the human:// basis carries the file id (operator-ratified consent id)
    public async Task Enroll_basis_is_human_rename_fileId()
    {
        var driveFiles = new Dictionary<string, string> { ["file-9"] = "Ada Lovelace" };
        var (resolver, _, candidates, consent, _, _, _) = Build(driveFiles);
        await candidates.ReplaceUnresolvedAsync([Candidate("file-9", "unknown_09", axis: 3)]);

        var result = await resolver.RunCycleAsync(On);

        Assert.Equal("ada-lovelace", Assert.Single(result.ResolvedSlugs));
        // The consent was recorded under the human://rename:<fileId> basis.
        Assert.True(await consent.IsConsentedAsync("ada-lovelace"));
    }

    [Fact] // scenario: only unresolved CANDIDATE files are ever acted on — an arbitrary Drive file is ignored
    public async Task Only_unresolved_candidate_files_are_acted_on()
    {
        // "stranger-file" is renamed but is NOT a candidate row → never enrolled.
        var driveFiles = new Dictionary<string, string> { ["file-3"] = "unknown_03.m4a", ["stranger-file"] = "Eve" };
        var (resolver, store, candidates, _, drive, _, _) = Build(driveFiles);
        await candidates.ReplaceUnresolvedAsync([Candidate("file-3", "unknown_03", axis: 1)]);

        var result = await resolver.RunCycleAsync(On);

        Assert.Empty(result.ResolvedSlugs);        // file-3 is unchanged (unknown_03.m4a); stranger has no row
        Assert.Null(await store.GetAsync("eve"));  // NEVER enrolled from an arbitrary Drive file
        Assert.Empty(drive.Moved);
    }

    [Fact] // scenario: an un-renamed file (still unknown_NN[.ext]) is skipped, never enrolled
    public async Task Unrenamed_file_is_skipped()
    {
        var driveFiles = new Dictionary<string, string> { ["file-3"] = "unknown_03.m4a" };
        var (resolver, store, candidates, _, drive, _, _) = Build(driveFiles);
        await candidates.ReplaceUnresolvedAsync([Candidate("file-3", "unknown_03", axis: 1)]);

        var result = await resolver.RunCycleAsync(On);

        Assert.Empty(result.ResolvedSlugs);
        Assert.Empty(drive.Moved);
        var row = await candidates.GetByDriveFileIdAsync("file-3");
        Assert.False(row!.Resolved);
    }

    [Fact] // scenario: a rename that slugifies empty (no valid name) is skipped — never fabricate a name
    public async Task Empty_slug_rename_is_skipped_never_fabricated()
    {
        var driveFiles = new Dictionary<string, string> { ["file-3"] = "!!!.m4a" };
        var (resolver, store, candidates, _, drive, _, _) = Build(driveFiles);
        await candidates.ReplaceUnresolvedAsync([Candidate("file-3", "unknown_03", axis: 1)]);

        var result = await resolver.RunCycleAsync(On);

        Assert.Empty(result.ResolvedSlugs);
        Assert.Single(result.Skipped);
        Assert.Empty(drive.Moved);
        var row = await candidates.GetByDriveFileIdAsync("file-3");
        Assert.False(row!.Resolved); // left unresolved (no enrollment happened)
    }

    [Fact] // scenario: a move failure leaves the candidate UNRESOLVED to retry (never partially-lose-track)
    public async Task Move_failure_leaves_candidate_unresolved_for_retry()
    {
        var driveFiles = new Dictionary<string, string> { ["file-3"] = "Marco" };
        var (resolver, store, candidates, _, drive, _, _) = Build(driveFiles);
        drive.FailMove = true;
        await candidates.ReplaceUnresolvedAsync([Candidate("file-3", "unknown_03", axis: 7)]);

        var result = await resolver.RunCycleAsync(On);

        Assert.Empty(result.ResolvedSlugs);
        // The enroll DID happen (idempotent refine on retry) but the row is NOT marked resolved.
        var row = await candidates.GetByDriveFileIdAsync("file-3");
        Assert.False(row!.Resolved);

        // Next cycle, with move now working, resolves it (idempotent retry).
        drive.FailMove = false;
        var second = await resolver.RunCycleAsync(On);
        Assert.Equal("marco", Assert.Single(second.ResolvedSlugs));
        Assert.Contains("file-3", drive.Moved);
        Assert.True((await candidates.GetByDriveFileIdAsync("file-3"))!.Resolved);
    }

    [Fact] // scenario: a Drive list failure (grant not widened) leaves everything unresolved, no throw
    public async Task List_failure_is_a_noop_retry()
    {
        var driveFiles = new Dictionary<string, string> { ["file-3"] = "Marco" };
        var (resolver, store, candidates, _, drive, _, _) = Build(driveFiles);
        drive.FailList = true;
        await candidates.ReplaceUnresolvedAsync([Candidate("file-3", "unknown_03", axis: 7)]);

        var result = await resolver.RunCycleAsync(On);

        Assert.Empty(result.ResolvedSlugs);
        Assert.Null(await store.GetAsync("marco"));
        Assert.False((await candidates.GetByDriveFileIdAsync("file-3"))!.Resolved);
    }

    [Fact] // scenario: an already-resolved candidate is never re-enrolled/re-moved (single-shot)
    public async Task Resolved_candidate_is_not_reprocessed()
    {
        var driveFiles = new Dictionary<string, string> { ["file-3"] = "Marco" };
        var (resolver, _, candidates, _, drive, _, _) = Build(driveFiles);
        await candidates.ReplaceUnresolvedAsync([Candidate("file-3", "unknown_03", axis: 7)]);

        await resolver.RunCycleAsync(On);        // resolves it, moves it out of the folder
        drive.Moved.Clear();
        // File is gone from the listing (moved to registry); a second cycle does nothing.
        var second = await resolver.RunCycleAsync(On);

        Assert.Empty(second.ResolvedSlugs);
        Assert.Empty(drive.Moved);
    }
}
