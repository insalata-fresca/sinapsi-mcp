using Cervello.Watcher.Domain;
using Cervello.Watcher.Normalize;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>recording-normalize — "Pair audio and transcript by basename".</summary>
public sealed class PairerTests
{
    private static StagedFile Staged(string basename, string kind, string fileId) =>
        new(basename, kind, fileId, "sha-" + fileId, DummyChange(fileId, basename + (kind == "audio" ? ".m4a" : ".txt")));

    private static DriveChange DummyChange(string id, string name) =>
        new(id, name, "application/octet-stream", "md5", 1,
            DateTimeOffset.Parse("2026-07-05T09:30:00Z"), DateTimeOffset.Parse("2026-07-05T09:30:00Z"),
            parents: new[] { "folder" }, removed: false, trashed: false);

    // ---- Scenario: Matching basenames pair ----
    [Fact]
    public void Matching_basenames_form_one_recording()
    {
        var p = new Pairer();
        Assert.Null(p.Offer(Staged("Foo", "audio", "A")));
        var pair = p.Offer(Staged("Foo", "transcript", "T"));

        Assert.NotNull(pair);
        Assert.Equal("Foo", pair!.Basename);
        Assert.Equal("A", pair.Audio.FileId);
        Assert.Equal("T", pair.Transcript.FileId);
    }

    // ---- Scenario: Lone audio waits for its transcript ----
    [Fact]
    public void Lone_audio_is_pending_no_pair()
    {
        var p = new Pairer();
        var pair = p.Offer(Staged("Foo", "audio", "A"));

        Assert.Null(pair);
        Assert.False(p.IsPaired("Foo"));
        Assert.Contains("Foo", p.Pending());
    }

    // ---- Scenario: Transcript arriving second completes the pair ----
    [Fact]
    public void Transcript_arriving_second_completes_the_pair()
    {
        var p = new Pairer();
        p.Offer(Staged("Foo", "audio", "A"));
        var pair = p.Offer(Staged("Foo", "transcript", "T"));

        Assert.NotNull(pair);
        Assert.True(p.IsPaired("Foo"));
        Assert.DoesNotContain("Foo", p.Pending());
    }

    // ---- Arrival order tolerance: transcript FIRST, audio second ----
    [Fact]
    public void Transcript_first_then_audio_still_pairs()
    {
        var p = new Pairer();
        Assert.Null(p.Offer(Staged("Foo", "transcript", "T")));
        var pair = p.Offer(Staged("Foo", "audio", "A"));

        Assert.NotNull(pair);
        Assert.Equal("A", pair!.Audio.FileId);
        Assert.Equal("T", pair.Transcript.FileId);
    }

    [Fact]
    public void Basename_of_strips_extension()
    {
        Assert.Equal("Foo", Pairer.BasenameOf("Foo.m4a"));
        Assert.Equal("Foo", Pairer.BasenameOf("Foo.txt"));
        Assert.Equal("2026-07-05 Meeting", Pairer.BasenameOf("2026-07-05 Meeting.m4a"));
    }

    [Fact]
    public void Distinct_basenames_do_not_pair()
    {
        var p = new Pairer();
        Assert.Null(p.Offer(Staged("Foo", "audio", "A")));
        Assert.Null(p.Offer(Staged("Bar", "transcript", "T"))); // different basename => no pair
    }
}
