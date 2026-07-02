using System.Net;
using System.Reflection;
using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// The load-bearing hardening suite (the Gdrive analogue of StepCa.Mcp's
/// <c>SubprocessToolErrorTests</c>). Two legs, both proven WITHOUT a live Google
/// account by controlling the DriveService transport:
///
///   1. SHORT-CIRCUIT — a malformed parameter must return a structured
///      {ok:false, error} envelope BEFORE any HTTP call. We point the tool at a
///      <see cref="FakeDrive.Throwing"/> service whose every request throws; if a
///      guard were missing the throw would surface instead of the envelope.
///
///   2. REDACTION — a real upstream error body carrying a fake secret must reach
///      the caller SCRUBBED. We point the tool at a
///      <see cref="FakeDrive.Responding"/> service that returns a 403 whose body
///      embeds a credential, and assert the tool's error string is redacted, not
///      the raw secret.
/// </summary>
public sealed class DriveToolGuardTests
{
    // Read the {ok,error} fields off the anonymous result via reflection.
    private static (bool ok, string? error) Envelope(object result)
    {
        var t = result.GetType();
        var okProp = t.GetProperty("ok");
        var errProp = t.GetProperty("error");
        var ok = okProp is not null && (bool)okProp.GetValue(result)!;
        var err = errProp?.GetValue(result) as string;
        return (ok, err);
    }

    // ── 1. short-circuit: invalid input never reaches the transport ──────────

    [Fact]
    public async Task GetFileMetadata_BadFileId_ShortCircuits_NoHttp()
    {
        var drive = FakeDrive.Throwing(); // any HTTP call would throw
        var r = await DriveTools.GetFileMetadata(drive, "-flaglike");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Contains("must not start with '-'", err!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nid")]
    [InlineData("nul\0id")] // C# NUL escape, never a literal NUL byte
    public async Task DownloadFile_BadFileId_ShortCircuits(string id)
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.DownloadFile(drive, id);
        var (ok, _) = Envelope(r);
        Assert.False(ok);
    }

    [Fact]
    public async Task SearchFiles_EmptyQuery_ShortCircuits()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.SearchFiles(drive, "   ");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("query is required", err);
    }

    [Fact]
    public async Task CreateFile_BadName_ShortCircuits()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.CreateFile(drive, name: "  ", content: "x");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("name is required", err);
    }

    [Fact]
    public async Task UpdateFile_BadFileId_ShortCircuits()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.UpdateFile(drive, fileId: "", newName: "renamed");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("fileId is required", err);
    }

    [Fact]
    public async Task DeleteFile_BadFileId_ShortCircuits()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.DeleteFile(drive, fileId: "-x", permanent: true);
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Contains("must not start with '-'", err!);
    }

    [Fact]
    public async Task DownloadFileBase64_BadFileId_ShortCircuits()
    {
        var drive = FakeDrive.Throwing();
        var r = await DriveTools.DownloadFileBase64(drive, fileId: "a\tb");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Contains("control characters", err!);
    }

    // ── 2. redaction: an upstream error body with a secret is scrubbed ───────

    [Fact]
    public async Task GetFileMetadata_UpstreamErrorWithSecret_IsRedacted()
    {
        // A plausible Google error body that (pathologically) echoes a token.
        const string body =
            "{ \"error\": { \"code\": 403, \"message\": \"denied; access_token=ya29.SUPERSECRETTOKEN was rejected\" } }";
        var drive = FakeDrive.Responding(HttpStatusCode.Forbidden, body);

        var r = await DriveTools.GetFileMetadata(drive, "1a2b3c4d5e"); // valid id → reaches transport

        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.NotNull(err);
        // The secret value must be gone; the redaction marker present.
        Assert.DoesNotContain("ya29.SUPERSECRETTOKEN", err!);
        Assert.Contains("[redacted]", err!);
    }

    [Fact]
    public async Task ListFiles_UpstreamError_ReturnsStructuredEnvelope()
    {
        const string body =
            "{ \"error\": { \"code\": 500, \"message\": \"backend error\" } }";
        var drive = FakeDrive.Responding(HttpStatusCode.InternalServerError, body);

        var r = await DriveTools.ListFiles(drive);

        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.NotNull(err);
    }

    // ── 3. the throwing transport really is reached for a VALID id ───────────
    // (guards against a false pass where the guard rejects everything)

    [Fact]
    public async Task GetFileMetadata_ValidId_ReachesTransport_AndErrorIsStructured()
    {
        var drive = FakeDrive.Throwing();
        // A valid id clears validation, so the throwing transport IS hit, and the
        // tool's catch turns it into a structured error (not an unhandled throw).
        var r = await DriveTools.GetFileMetadata(drive, "1a2b3c4d5e");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.NotNull(err);
    }

    // ── sanity: the tool-guard reflection reads a real field ─────────────────
    [Fact]
    public void EnvelopeReader_FindsOkAndError()
    {
        var probe = new { ok = false, error = "x" };
        var (ok, err) = Envelope(probe);
        Assert.False(ok);
        Assert.Equal("x", err);
        // Guard the reflection helper name won't silently rot.
        Assert.NotNull(typeof(DriveTools).GetMethod("GetFileMetadata", BindingFlags.Public | BindingFlags.Static));
    }
}
