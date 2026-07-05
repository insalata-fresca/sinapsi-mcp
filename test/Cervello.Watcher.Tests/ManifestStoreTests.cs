using Cervello.Watcher.Domain;
using Cervello.Watcher.Normalize;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// recording-normalize — "Manifest registration is idempotent and custody-safe".
/// </summary>
public sealed class ManifestStoreTests
{
    private static ManifestEntry Entry(string id) => new(
        id: id,
        audioSha256: "abc123",
        sourceDriveId: "drive-audio-id",
        transcript: $"recordings/transcripts/{id}.md",
        googleTxt: "drive-txt-id",
        attribution: "pending",
        recordedAt: "2026-07-05T09:30",
        state: "normalized");

    // ---- Scenario: First normalize writes one entry ----
    [Fact]
    public async Task First_append_writes_exactly_one_entry_with_state_normalized()
    {
        using var ws = new TempWorkspace();
        var store = new YamlManifestStore(ws.ManifestPath);

        var changed = await store.AppendAsync(Entry("20260705-foo"), default);

        Assert.True(changed);
        var text = File.ReadAllText(ws.ManifestPath);
        Assert.Single(CountEntries(text)); // exactly one entry
        Assert.Contains("- id: 20260705-foo", text);
        Assert.Contains("state: normalized", text);
        Assert.DoesNotContain("[]", text); // the lone empty list was replaced
    }

    // ---- Scenario: Entry carries the schema-required fields ----
    [Fact]
    public async Task Entry_carries_all_section8_fields_in_order()
    {
        using var ws = new TempWorkspace();
        var store = new YamlManifestStore(ws.ManifestPath);
        await store.AppendAsync(Entry("20260705-foo"), default);

        var lines = File.ReadAllLines(ws.ManifestPath)
            .Where(l => l.TrimStart().Length > 0 && !l.TrimStart().StartsWith('#'))
            .ToArray();
        // The §8 field order is fixed.
        Assert.Contains("- id: 20260705-foo", lines[0]);
        Assert.Contains("audio_sha256: abc123", lines[1]);
        Assert.Contains("source_drive_id: drive-audio-id", lines[2]);
        Assert.Contains("transcript: recordings/transcripts/20260705-foo.md", lines[3]);
        Assert.Contains("google_txt: drive-txt-id", lines[4]);
        Assert.Contains("attribution: pending", lines[5]);
        Assert.Contains("recorded_at: 2026-07-05T09:30", lines[6]);
        Assert.Contains("state: normalized", lines[7]);
    }

    // ---- Scenario: Second run is a no-op (byte-unchanged) ----
    [Fact]
    public async Task Re_appending_the_same_id_is_a_byte_unchanged_no_op()
    {
        using var ws = new TempWorkspace();
        var store = new YamlManifestStore(ws.ManifestPath);
        await store.AppendAsync(Entry("20260705-foo"), default);
        var bytesAfterFirst = File.ReadAllBytes(ws.ManifestPath);

        var changed = await store.AppendAsync(Entry("20260705-foo"), default);

        Assert.False(changed); // no-op
        var bytesAfterSecond = File.ReadAllBytes(ws.ManifestPath);
        Assert.Equal(bytesAfterFirst, bytesAfterSecond); // BYTE-identical
    }

    [Fact]
    public async Task Two_distinct_ids_append_two_blocks_and_preserve_the_first()
    {
        using var ws = new TempWorkspace();
        var store = new YamlManifestStore(ws.ManifestPath);
        await store.AppendAsync(Entry("20260705-foo"), default);
        var afterFirst = File.ReadAllText(ws.ManifestPath);

        var changed = await store.AppendAsync(Entry("20260705-bar"), default);

        Assert.True(changed);
        var text = File.ReadAllText(ws.ManifestPath);
        Assert.Contains("- id: 20260705-foo", text);
        Assert.Contains("- id: 20260705-bar", text);
        // The first block's content is still intact.
        Assert.StartsWith(afterFirst.TrimEnd('\n'), text.TrimEnd('\n'));
    }

    [Fact]
    public async Task Header_comment_is_preserved_across_appends()
    {
        using var ws = new TempWorkspace();
        var store = new YamlManifestStore(ws.ManifestPath);
        await store.AppendAsync(Entry("20260705-foo"), default);
        var text = File.ReadAllText(ws.ManifestPath);
        Assert.StartsWith("#", text); // leading comment header preserved
    }

    [Fact]
    public void ContainsId_matches_only_exact_ids()
    {
        const string yaml = "- id: 20260705-foo\n  audio_sha256: x\n";
        Assert.True(YamlManifestStore.ContainsId(yaml, "20260705-foo"));
        Assert.False(YamlManifestStore.ContainsId(yaml, "20260705-fo"));   // not a prefix match
        Assert.False(YamlManifestStore.ContainsId(yaml, "20260705-foobar"));
    }

    private static string[] CountEntries(string text) =>
        text.Split('\n').Where(l => l.TrimStart().StartsWith("- id:")).ToArray();
}
