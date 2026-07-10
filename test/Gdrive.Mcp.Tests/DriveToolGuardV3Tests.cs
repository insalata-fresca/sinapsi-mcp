using System.Net;
using System.Text;
using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// The V3 analogue of <see cref="DriveToolGuardTests"/> for the three tools added
/// for docs/design/voiceprint-naming.md §7/§8: <c>create_folder</c>,
/// <c>upload_file</c>, <c>move_file</c>. Same two hardening legs as the base
/// suite (short-circuit before HTTP; upstream-error redaction), PLUS a
/// byte-exact round-trip proof for the binary upload path — the whole reason
/// <c>upload_file</c> exists over the text-only <c>create_file</c>.
/// </summary>
public sealed class DriveToolGuardV3Tests
{
    private static (bool ok, string? error) Envelope(object result)
    {
        var t = result.GetType();
        var okProp = t.GetProperty("ok");
        var errProp = t.GetProperty("error");
        var ok = okProp is not null && (bool)okProp.GetValue(result)!;
        var err = errProp?.GetValue(result) as string;
        return (ok, err);
    }

    // A canned Drive Files resource JSON body, valid enough for the client to deserialize.
    private const string CannedFileJson =
        "{ \"id\": \"newid123\", \"name\": \"unknown_03.m4a\", \"mimeType\": \"audio/mp4\" }";

    // ── 1. short-circuit: invalid input never reaches the transport ──────────

    [Fact]
    public async Task CreateFolder_BadName_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.CreateFolder(drive, name: "   ");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("name is required", err);
    }

    [Fact]
    public async Task CreateFolder_BadParentFolderId_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.CreateFolder(drive, name: "voiceprints", parentFolderId: "-flaglike");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Contains("must not start with '-'", err!);
    }

    [Fact]
    public async Task UploadFile_BadName_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.UploadFile(drive, name: "", contentBase64: Convert.ToBase64String(new byte[] { 1 }), mimeType: "audio/mp4");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("name is required", err);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!!")]
    public async Task UploadFile_BadContentBase64_ShortCircuits_NoHttp(string? content)
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.UploadFile(drive, name: "unknown_03.m4a", contentBase64: content!, mimeType: "audio/mp4");
        var (ok, _) = Envelope(r);
        Assert.False(ok);
    }

    [Fact]
    public async Task UploadFile_BadMimeType_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.UploadFile(drive, name: "unknown_03.m4a", contentBase64: Convert.ToBase64String(new byte[] { 1, 2 }), mimeType: "");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("mimeType is required", err);
    }

    [Fact]
    public async Task UploadFile_BadFolderId_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.UploadFile(drive, name: "unknown_03.m4a",
            contentBase64: Convert.ToBase64String(new byte[] { 1, 2 }), mimeType: "audio/mp4", folderId: "-x");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Contains("must not start with '-'", err!);
    }

    [Fact]
    public async Task MoveFile_BadFileId_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.MoveFile(drive, fileId: "-x", destFolderId: "regfolder");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Contains("must not start with '-'", err!);
    }

    [Fact]
    public async Task MoveFile_NeitherDestNorRemove_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.MoveFile(drive, fileId: "1a2b3c4d5e");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("at least one of destFolderId or removeFolderId is required", err);
    }

    [Fact]
    public async Task MoveFile_BadDestFolderId_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.MoveFile(drive, fileId: "1a2b3c4d5e", destFolderId: "-bad");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Contains("destFolderId", err!);
        Assert.Contains("must not start with '-'", err!);
    }

    [Fact]
    public async Task MoveFile_BadRemoveFolderId_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.MoveFile(drive, fileId: "1a2b3c4d5e", removeFolderId: "bad\nid");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Contains("removeFolderId", err!);
        Assert.Contains("control characters", err!);
    }

    // ── 2. redaction: an upstream error body with a secret is scrubbed ───────

    [Fact]
    public async Task CreateFolder_UpstreamErrorWithSecret_IsRedacted()
    {
        const string body =
            "{ \"error\": { \"code\": 403, \"message\": \"denied; refresh_token=1//0gSUPERSECRET was rejected\" } }";
        var drive = FakeDrive.Responding(HttpStatusCode.Forbidden, body);

        var r = await DriveTools.CreateFolder(drive, name: "voiceprints");

        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.NotNull(err);
        Assert.DoesNotContain("1//0gSUPERSECRET", err!);
        Assert.Contains("[redacted]", err!);
    }

    [Fact]
    public async Task MoveFile_UpstreamError_ReturnsStructuredEnvelope()
    {
        const string body = "{ \"error\": { \"code\": 500, \"message\": \"backend error\" } }";
        var drive = FakeDrive.Responding(HttpStatusCode.InternalServerError, body);

        var r = await DriveTools.MoveFile(drive, fileId: "1a2b3c4d5e", destFolderId: "regfolder");

        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.NotNull(err);
    }

    // ── 3. success round-trips against a fake transport ──────────────────────

    [Fact]
    public async Task CreateFolder_ValidInput_ReturnsSummary()
    {
        var drive = FakeDrive.Responding(HttpStatusCode.OK,
            "{ \"id\": \"folder123\", \"name\": \"voiceprints\", \"mimeType\": \"application/vnd.google-apps.folder\" }");

        var r = await DriveTools.CreateFolder(drive, name: "voiceprints", parentFolderId: "root123");

        // Success shape: a Summarize() object, never the {ok:false,error} envelope.
        Assert.Null(r.GetType().GetProperty("error"));
        var idProp = r.GetType().GetProperty("id");
        Assert.Equal("folder123", idProp!.GetValue(r));
    }

    [Fact]
    public async Task MoveFile_ValidInput_ReturnsSummary()
    {
        var drive = FakeDrive.Responding(HttpStatusCode.OK,
            "{ \"id\": \"1a2b3c4d5e\", \"name\": \"Marco.m4a\", \"parents\": [\"registryFolderId\"] }");

        var r = await DriveTools.MoveFile(drive, fileId: "1a2b3c4d5e", destFolderId: "registryFolderId", removeFolderId: "voiceprintsFolderId");

        Assert.Null(r.GetType().GetProperty("error"));
        var idProp = r.GetType().GetProperty("id");
        Assert.Equal("1a2b3c4d5e", idProp!.GetValue(r));
    }

    // ── 4. byte-exact upload proof ────────────────────────────────────────────

    [Fact]
    public async Task UploadFile_ValidInput_UploadsDecodedBytesByteExact()
    {
        // A representative ~30s-clip-shaped payload (not literally 30s of audio —
        // just non-trivial random bytes to prove no mangling/truncation/re-encoding).
        var originalBytes = new byte[8192];
        new Random(7).NextBytes(originalBytes);
        var contentBase64 = Convert.ToBase64String(originalBytes);

        var captured = new List<CapturedRequest>();
        var drive = FakeDrive.Capturing(captured, CannedFileJson);

        var r = await DriveTools.UploadFile(
            drive, name: "unknown_03.m4a", contentBase64: contentBase64, mimeType: "audio/mp4", folderId: "voiceprintsFolderId");

        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Null(err); // success shape: no "error" prop at all

        var idProp = r.GetType().GetProperty("id");
        Assert.Equal("newid123", idProp!.GetValue(r));

        // At least one captured request's body (the resumable-upload PUT leg
        // carrying the actual bytes — see FakeDrive.Capturing) must contain the
        // ORIGINAL bytes verbatim, proving upload_file decodes+uploads byte-exact.
        Assert.Contains(captured, c => ContainsSubsequence(c.Body, originalBytes));
    }

    [Fact]
    public async Task UploadFile_EmptyFolderId_OmitsParents()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0xFF };
        var captured = new List<CapturedRequest>();
        var drive = FakeDrive.Capturing(captured, CannedFileJson);

        var r = await DriveTools.UploadFile(drive, name: "unknown_04.m4a", contentBase64: Convert.ToBase64String(bytes), mimeType: "audio/mp4");

        var idProp = r.GetType().GetProperty("id");
        Assert.Equal("newid123", idProp!.GetValue(r));
        Assert.Contains(captured, c => ContainsSubsequence(c.Body, bytes));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    // ── sanity: existing tools untouched — a couple of spot checks ───────────

    [Fact]
    public async Task CreateFile_StillWorks_UnaffectedByV3Additions()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.CreateFile(drive, name: "  ", content: "x");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("name is required", err);
    }

    [Fact]
    public async Task UpdateFile_StillWorks_UnaffectedByV3Additions()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.UpdateFile(drive, fileId: "", newName: "renamed");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("fileId is required", err);
    }
}
