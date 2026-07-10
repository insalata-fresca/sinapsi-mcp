using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// <see cref="IVoiceprintNamingCandidateStore"/> contract tests (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V4, §4.4) against
/// <see cref="InMemoryVoiceprintNamingCandidateStore"/> — the same contract
/// <see cref="Adapters.PgVoiceprintNamingCandidateStore"/> honours (its SQL is asserted by review, no
/// live DB in this suite). SYNTHETIC 192-d vectors only.
/// </summary>
public sealed class VoiceprintNamingCandidateStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    private static VoiceprintNamingCandidate Candidate(
        string sampleName, string driveFileId, float[]? centroid = null, bool resolved = false) =>
        new(sampleName, driveFileId, centroid ?? TestVectors.Axis(0),
            [new VoiceReviewMember("rec-1", 0, 12.0, 3)], T0, resolved);

    [Fact]
    public async Task Replace_then_get_unresolved_round_trips()
    {
        var store = new InMemoryVoiceprintNamingCandidateStore();
        await store.ReplaceUnresolvedAsync([
            Candidate("unknown_01", "drive-1"),
            Candidate("unknown_02", "drive-2", TestVectors.Axis(5)),
        ]);

        var got = await store.GetUnresolvedAsync();
        Assert.Equal(2, got.Count);
        Assert.Equal("unknown_01", got[0].SampleName); // ordered by SampleName
        Assert.Equal("unknown_02", got[1].SampleName);
    }

    [Fact]
    public async Task GetByDriveFileId_returns_null_for_unknown_id()
    {
        var store = new InMemoryVoiceprintNamingCandidateStore();
        Assert.Null(await store.GetByDriveFileIdAsync("nope"));
    }

    [Fact]
    public async Task Replace_deletes_prior_unresolved_and_returns_their_ids()
    {
        var store = new InMemoryVoiceprintNamingCandidateStore();
        await store.ReplaceUnresolvedAsync([Candidate("unknown_01", "drive-1")]);

        var deleted = await store.ReplaceUnresolvedAsync([Candidate("unknown_01", "drive-2")]);

        Assert.Equal(["drive-1"], deleted);
        var unresolved = await store.GetUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal("drive-2", unresolved[0].DriveFileId);
    }

    [Fact]
    public async Task Replace_never_deletes_or_returns_a_resolved_row()
    {
        var store = new InMemoryVoiceprintNamingCandidateStore();
        await store.ReplaceUnresolvedAsync([Candidate("unknown_01", "drive-1")]);
        Assert.True(await store.MarkResolvedAsync("drive-1"));

        var deleted = await store.ReplaceUnresolvedAsync([Candidate("unknown_02", "drive-2")]);

        Assert.Empty(deleted); // the resolved row was never touched/deleted
        var resolved = await store.GetByDriveFileIdAsync("drive-1");
        Assert.NotNull(resolved);
        Assert.True(resolved!.Resolved);
    }

    [Fact]
    public async Task MarkResolved_is_a_noop_for_unknown_or_already_resolved()
    {
        var store = new InMemoryVoiceprintNamingCandidateStore();
        Assert.False(await store.MarkResolvedAsync("nope"));

        await store.ReplaceUnresolvedAsync([Candidate("unknown_01", "drive-1")]);
        Assert.True(await store.MarkResolvedAsync("drive-1"));
        Assert.False(await store.MarkResolvedAsync("drive-1")); // already resolved -> false, not a throw
    }

    [Fact]
    public void Candidate_requires_192_d_centroid()
    {
        Assert.Throws<ArgumentException>(() => new VoiceprintNamingCandidate(
            "unknown_01", "drive-1", new float[10], [new VoiceReviewMember("rec-1", 0, 1, 1)], T0));
    }

    [Fact]
    public void Candidate_requires_at_least_one_source_member()
    {
        Assert.Throws<ArgumentException>(() => new VoiceprintNamingCandidate(
            "unknown_01", "drive-1", TestVectors.Axis(0), [], T0));
    }
}

/// <summary><see cref="Adapters.InMemoryRecordingAudioRefResolver"/> contract tests.</summary>
public sealed class RecordingAudioRefResolverTests
{
    [Fact]
    public async Task Resolves_a_seeded_recording()
    {
        var resolver = new InMemoryRecordingAudioRefResolver()
            .Add(new Ports.RecordingAudioRef("rec-1", "sha-abc", "m4a"));

        var r = await resolver.ResolveAsync("rec-1");

        Assert.NotNull(r);
        Assert.Equal("sha-abc", r!.AudioSha256);
        Assert.Equal("m4a", r.Format);
    }

    [Fact]
    public async Task Returns_null_for_unknown_recording()
    {
        var resolver = new InMemoryRecordingAudioRefResolver();
        Assert.Null(await resolver.ResolveAsync("unknown"));
    }
}
