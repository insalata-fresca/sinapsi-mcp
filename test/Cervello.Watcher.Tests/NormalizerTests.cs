using Cervello.Watcher.Domain;
using Cervello.Watcher.Normalize;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>recording-normalize — "Deterministic recording id" + recorded_at.</summary>
public sealed class NormalizerTests
{
    private static PairedRecording Pair(string basename, string audioSha, DateTimeOffset created)
    {
        var audioChange = new DriveChange("A-" + basename, basename + ".m4a", "audio/mp4", "md5a", 10,
            created, created, new[] { "folder" }, false, false);
        var txtChange = new DriveChange("T-" + basename, basename + ".txt", "text/plain", "md5t", 5,
            created, created, new[] { "folder" }, false, false);
        var audio = new StagedFile(basename, "audio", audioChange.FileId, audioSha, audioChange);
        var txt = new StagedFile(basename, "transcript", txtChange.FileId, "sha-t", txtChange);
        return new PairedRecording(basename, audio, txt);
    }

    // ---- Scenario: Same input yields same id ----
    [Fact]
    public void Same_input_yields_same_id_and_recorded_at()
    {
        var n = new Normalizer();
        var when = DateTimeOffset.Parse("2026-07-05T09:30:00Z");
        var r1 = n.Normalize(Pair("Foo", "sha-audio", when));
        var r2 = n.Normalize(Pair("Foo", "sha-audio", when));

        Assert.Equal(r1.Id, r2.Id);
        Assert.Equal(r1.RecordedAt, r2.RecordedAt);
        Assert.Equal("20260705-foo", r1.Id);
        Assert.Equal("2026-07-05T09:30", r1.RecordedAt);
    }

    // ---- Scenario: Distinct recordings get distinct ids ----
    [Fact]
    public void Distinct_basenames_get_distinct_ids()
    {
        var n = new Normalizer();
        var when = DateTimeOffset.Parse("2026-07-05T09:30:00Z");
        var a = n.Normalize(Pair("Foo", "sha1", when));
        var b = n.Normalize(Pair("Bar", "sha2", when));
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Distinct_dates_get_distinct_ids()
    {
        var n = new Normalizer();
        var a = n.Normalize(Pair("Foo", "sha1", DateTimeOffset.Parse("2026-07-05T09:30:00Z")));
        var b = n.Normalize(Pair("Foo", "sha1", DateTimeOffset.Parse("2026-07-06T09:30:00Z")));
        Assert.NotEqual(a.Id, b.Id);
        Assert.StartsWith("20260705-", a.Id);
        Assert.StartsWith("20260706-", b.Id);
    }

    [Theory]
    [InlineData("Foo", "foo")]
    [InlineData("My Meeting", "my-meeting")]
    [InlineData("Réunion café", "reunion-cafe")]     // ASCII-fold diacritics
    [InlineData("A  --  B", "a-b")]                    // collapse non-alnum runs
    [InlineData("2026_07_05__notes", "2026-07-05-notes")]
    [InlineData("!!!", "recording")]                  // empty slug => fallback
    public void Slug_is_deterministic_lowercase_ascii_hyphenated(string input, string expected)
    {
        Assert.Equal(expected, Normalizer.Slug(input));
    }

    [Fact]
    public void RecordedAt_is_minute_precision_utc()
    {
        var when = DateTimeOffset.Parse("2026-07-05T09:30:45+02:00"); // 07:30 UTC
        Assert.Equal("2026-07-05T07:30", Normalizer.FormatRecordedAt(when));
    }
}
