using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// Unit tests for the V3 validators added to <see cref="GdriveValidation"/> for
/// docs/design/voiceprint-naming.md §7/§8: <c>ValidateBase64Content</c> (backs
/// <c>upload_file</c>'s <c>contentBase64</c>) and <c>ValidateRequiredParentId</c>
/// (backs <c>move_file</c>'s <c>destFolderId</c>/<c>removeFolderId</c>). Mirrors
/// the accept/reject-matrix style of <see cref="GdriveValidationTests"/>.
/// </summary>
public sealed class GdriveValidationV3Tests
{
    // ── base64 content (upload_file) ───────────────────────────────────────

    [Fact]
    public void ValidateBase64Content_AcceptsValid() =>
        Assert.Null(GdriveValidation.ValidateBase64Content(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));

    [Fact]
    public void ValidateBase64Content_AcceptsEmptyByteArrayEncoded() =>
        // Convert.ToBase64String(Array.Empty<byte>()) is "" — an empty upload is
        // rejected as "required" (mirrors ValidateContent's null-vs-empty split:
        // an upload always carries SOME bytes, unlike create_file's legitimate
        // empty text file), so this is intentionally covered under RejectsEmpty.
        Assert.Equal("contentBase64 is required", GdriveValidation.ValidateBase64Content(""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateBase64Content_RejectsEmpty(string? content) =>
        Assert.Equal("contentBase64 is required", GdriveValidation.ValidateBase64Content(content));

    [Fact]
    public void ValidateBase64Content_RejectsOversize()
    {
        // Build an over-ceiling but still well-formed base64 string (length a multiple of 4).
        var overLen = GdriveValidation.MaxBase64ContentLength + 4;
        var big = new string('A', overLen);
        Assert.Contains("too long", GdriveValidation.ValidateBase64Content(big)!);
    }

    [Theory]
    [InlineData("not-valid-base64!!!")]
    [InlineData("QQ")] // wrong padding length
    [InlineData("****")]
    public void ValidateBase64Content_RejectsMalformed(string bad) =>
        Assert.Equal("contentBase64 is not well-formed base64", GdriveValidation.ValidateBase64Content(bad));

    [Fact]
    public void ValidateBase64Content_RoundTripsRealAudioLikeBytes()
    {
        var bytes = new byte[1024];
        new Random(42).NextBytes(bytes);
        var b64 = Convert.ToBase64String(bytes);
        Assert.Null(GdriveValidation.ValidateBase64Content(b64));
        Assert.Equal(bytes, Convert.FromBase64String(b64));
    }

    // ── required parent id (move_file) ──────────────────────────────────────

    [Fact]
    public void ValidateRequiredParentId_AcceptsValid() =>
        Assert.Null(GdriveValidation.ValidateRequiredParentId("0AElfWBcd", "destFolderId"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRequiredParentId_RejectsEmpty(string? id) =>
        Assert.Equal("destFolderId is required", GdriveValidation.ValidateRequiredParentId(id, "destFolderId"));

    [Fact]
    public void ValidateRequiredParentId_RejectsTooLong()
    {
        var id = new string('a', GdriveValidation.MaxIdLength + 1);
        Assert.Contains("too long", GdriveValidation.ValidateRequiredParentId(id, "removeFolderId")!);
    }

    [Fact]
    public void ValidateRequiredParentId_RejectsControlChars() =>
        Assert.Contains("control characters", GdriveValidation.ValidateRequiredParentId("a\nb", "destFolderId")!);

    [Fact]
    public void ValidateRequiredParentId_RejectsLeadingDash() =>
        Assert.Contains("must not start with '-'", GdriveValidation.ValidateRequiredParentId("-x", "destFolderId")!);

    [Fact]
    public void ValidateRequiredParentId_MessageNamesTheField()
    {
        Assert.StartsWith("removeFolderId", GdriveValidation.ValidateRequiredParentId(null, "removeFolderId"));
        Assert.StartsWith("destFolderId", GdriveValidation.ValidateRequiredParentId(null, "destFolderId"));
    }
}
