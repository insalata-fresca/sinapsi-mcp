using Cervello.Watcher.Domain;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>immutable-domain-types — constructor invariants + key derivation.</summary>
public sealed class DomainTests
{
    [Fact]
    public void DriveChange_requires_a_non_empty_fileId()
    {
        Assert.Throws<ArgumentException>(() => new DriveChange(
            "", "n", "m", "md5", 1, null, null, null, false, false));
    }

    [Fact]
    public void DriveChange_key_is_drive_fileId_md5()
    {
        var c = new DriveChange("F", "Foo.m4a", "audio/mp4", "MD5X", 1, null, null, null, false, false);
        Assert.Equal("drive:F:MD5X", c.DriveKey);
    }

    [Fact]
    public void Recording_key_is_rec_id_audioSha()
    {
        var r = new Recording("20260705-foo", "Foo", "SHA1", "A", "T", "2026-07-05T09:30", PipelineState.Normalized);
        Assert.Equal("rec:20260705-foo:SHA1", r.RecordingKey);
    }

    [Fact]
    public void Recording_rejects_empty_required_fields()
    {
        Assert.Throws<ArgumentException>(() =>
            new Recording("", "Foo", "SHA1", "A", "T", "2026-07-05T09:30", PipelineState.Normalized));
        Assert.Throws<ArgumentException>(() =>
            new Recording("id", "Foo", "", "A", "T", "2026-07-05T09:30", PipelineState.Normalized));
    }

    [Fact]
    public void ManifestEntry_for_recording_maps_section8_fields()
    {
        var r = new Recording("20260705-foo", "Foo", "SHA1", "A", "T", "2026-07-05T09:30", PipelineState.Normalized);
        var e = ManifestEntry.ForRecording(r);

        Assert.Equal("20260705-foo", e.Id);
        Assert.Equal("SHA1", e.AudioSha256);
        Assert.Equal("A", e.SourceDriveId);                                   // audio drive id
        Assert.Equal("recordings/transcripts/20260705-foo.md", e.Transcript); // planned ENRICH output (D6)
        Assert.Equal("T", e.GoogleTxt);                                       // txt drive id (content hint)
        Assert.Equal("pending", e.Attribution);
        Assert.Equal("2026-07-05T09:30", e.RecordedAt);
        Assert.Equal("normalized", e.State);
    }
}
