using System.Reflection;
using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// The capability-ticket store behind <c>download_to_url</c>: unguessable tokens, TTL
/// expiry, single-use-window semantics — plus the filename sanitiser that guards the
/// streaming endpoint's Content-Disposition header.
/// </summary>
public sealed class DownloadTicketStoreTests
{
    [Fact]
    public void Issue_ThenTryGet_RoundTrips()
    {
        var store = new DownloadTicketStore();
        var t = store.Issue("file-1", "report.pdf", 1234, TimeSpan.FromMinutes(5));

        Assert.Equal(32, t.Token.Length);                 // 16 random bytes hex-encoded
        Assert.Matches("^[0-9a-f]+$", t.Token);           // lowercase hex, unguessable
        Assert.True(store.TryGet(t.Token, out var got));
        Assert.Equal("file-1", got.FileId);
        Assert.Equal("report.pdf", got.Name);
        Assert.Equal(1234, got.Size);
    }

    [Fact]
    public void TryGet_UnknownToken_Fails()
    {
        var store = new DownloadTicketStore();
        Assert.False(store.TryGet("deadbeef", out _));
    }

    [Fact]
    public void TryGet_ExpiredTicket_Fails()
    {
        var store = new DownloadTicketStore();
        var t = store.Issue("file-2", "x", 1, TimeSpan.FromMilliseconds(-1)); // already expired
        Assert.False(store.TryGet(t.Token, out _));
    }

    [Fact]
    public void TokensAreUnique()
    {
        var store = new DownloadTicketStore();
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
            Assert.True(seen.Add(store.Issue("f", null, null, TimeSpan.FromMinutes(1)).Token));
    }

    [Theory]
    [InlineData("clean.bin", "clean.bin")]
    [InlineData("a/b\\c.txt", "a_b_c.txt")]
    [InlineData("evil\"name\r\n.img", "evil_name__.img")]
    public void SanitizeFilename_StripsDangerousChars(string input, string expected)
    {
        var m = typeof(DriveDownloadEndpoints).GetMethod(
            "SanitizeFilename", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (string)m.Invoke(null, new object[] { input })!);
    }
}
