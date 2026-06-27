using System.Text;
using Sinapsi.Forge.Model;
using Sinapsi.Forge.Tools;
using Xunit;

namespace Forge.Tests;

/// <summary>The raw-text content path: a caller with no shell passes plain UTF-8 text and
/// the tool base64-encodes it server-side, byte-exact. content_base64 stays for binary;
/// the two are mutually exclusive.</summary>
public sealed class ContentToolsTests
{
    [Fact]
    public void ResolveBase64_RawText_EncodesByteExact()
    {
        const string text = "# Execution reliability\nFiles via the Forgejo MCP — café ✓ é\n";
        var b64 = ContentTools.ResolveBase64(text, null);
        Assert.Equal(text, Encoding.UTF8.GetString(Convert.FromBase64String(b64))); // round-trips byte-exact
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), b64);
    }

    [Fact]
    public void ResolveBase64_EmptyRawText_IsEmptyFile()
        => Assert.Equal("", ContentTools.ResolveBase64("", null)); // empty string -> empty file, not "missing"

    [Fact]
    public void ResolveBase64_Base64_PassesThroughVerbatim()
    {
        byte[] raw = { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0xFE, 0x0A }; // binary, non-UTF-8
        var b64 = Convert.ToBase64String(raw);
        Assert.Equal(b64, ContentTools.ResolveBase64(null, b64));
    }

    [Fact]
    public void ResolveBase64_NeitherOrBoth_Throws()
    {
        Assert.Throws<ArgumentException>(() => ContentTools.ResolveBase64(null, null));
        Assert.Throws<ArgumentException>(() => ContentTools.ResolveBase64("x", "eA=="));
    }

    [Fact]
    public void NormalizeFiles_EncodesRawContent_LeavesBase64AndDeletes()
    {
        const string text = "hello world\n";
        var files = new List<ForgeFileChange>
        {
            new("a.txt", "create", ContentBase64: null, Sha: null, FromPath: null, Content: text),         // raw -> encode
            new("b.bin", "update", ContentBase64: "QQ==", Sha: "deadbeef", FromPath: null),                 // binary base64 -> passthrough
            new("c.txt", "delete", ContentBase64: null, Sha: "cafef00d", FromPath: null),                   // delete -> passthrough
        };

        var norm = ContentTools.NormalizeFiles(files);

        // a.txt: raw text encoded byte-exact, Content cleared.
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), norm[0].ContentBase64);
        Assert.Null(norm[0].Content);
        // b.bin: untouched binary base64.
        Assert.Equal("QQ==", norm[1].ContentBase64);
        // c.txt: delete carries no content.
        Assert.Equal("delete", norm[2].Operation);
        Assert.Null(norm[2].ContentBase64);
    }

    [Fact]
    public void NormalizeFiles_BothContentAndBase64_Throws()
    {
        var files = new List<ForgeFileChange>
        {
            new("x.txt", "create", ContentBase64: "QQ==", Sha: null, FromPath: null, Content: "raw"),
        };
        Assert.Throws<ArgumentException>(() => ContentTools.NormalizeFiles(files));
    }
}
