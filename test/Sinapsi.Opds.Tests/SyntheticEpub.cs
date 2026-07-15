using System.IO.Compression;
using System.Text;

namespace Sinapsi.Opds.Tests;

/// <summary>
/// Builds minimal, VALID synthetic EPUB zips in-memory for extractor tests, so
/// NO licensed content is committed. Two flavours: EPUB3 (nav.xhtml TOC) and
/// EPUB2 (toc.ncx TOC). The xhtml deliberately includes &amp;nbsp; entities,
/// a &lt;style&gt; and &lt;script&gt; block to strip, and multi-level headings to
/// prove section splitting.
/// </summary>
internal static class SyntheticEpub
{
    private const string ContainerXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    // Chapter 1: h1 + two h2 sections; a <style> + <script> to be stripped; &nbsp;.
    private const string Chapter1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head>
          <title>Chapter One Title Tag</title>
          <style>body { color: red; } h1 { font-size: 2rem; }</style>
        </head>
        <body>
          <h1>Introduction</h1>
          <p>This is the&nbsp;first paragraph with a non-breaking space.</p>
          <p>Second paragraph &amp; an ampersand.</p>
          <h2>Background</h2>
          <p>Background text under the second-level heading.</p>
          <script>console.log('should not appear');</script>
          <h2>Motivation</h2>
          <p>Motivation text here.</p>
        </body>
        </html>
        """;

    // Chapter 2 (EPUB2-style): an unclosed <br> void tag that XDocument would reject.
    private const string Chapter2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>Chapter Two</title></head>
        <body>
          <h1>Second Chapter</h1>
          <p>A line<br>with a break.</p>
          <p>Another paragraph.</p>
        </body>
        </html>
        """;

    private const string Nav = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><title>Contents</title></head>
        <body>
          <nav epub:type="toc" id="toc">
            <ol>
              <li><a href="ch1.xhtml">Chapter One (from nav)</a></li>
              <li><a href="ch2.xhtml">Chapter Two (from nav)</a></li>
            </ol>
          </nav>
        </body>
        </html>
        """;

    private const string Ncx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
          <head/>
          <docTitle><text>Synthetic EPUB2 Book</text></docTitle>
          <navMap>
            <navPoint id="np1" playOrder="1">
              <navLabel><text>Chapter One (from ncx)</text></navLabel>
              <content src="ch1.xhtml"/>
            </navPoint>
            <navPoint id="np2" playOrder="2">
              <navLabel><text>Chapter Two (from ncx)</text></navLabel>
              <content src="ch2.xhtml"/>
            </navPoint>
          </navMap>
        </ncx>
        """;

    private static string Opf(bool epub3) => epub3
        ? """
          <?xml version="1.0" encoding="UTF-8"?>
          <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
            <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
              <dc:identifier id="pub-id">urn:isbn:9990000000099</dc:identifier>
              <dc:title>Synthetic EPUB3 Book</dc:title>
              <dc:language>en</dc:language>
              <dc:creator>Test Author</dc:creator>
            </metadata>
            <manifest>
              <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
              <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
              <item id="ch2" href="ch2.xhtml" media-type="application/xhtml+xml"/>
            </manifest>
            <spine>
              <itemref idref="ch1"/>
              <itemref idref="ch2"/>
            </spine>
          </package>
          """
        : """
          <?xml version="1.0" encoding="UTF-8"?>
          <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="pub-id">
            <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
              <dc:identifier id="pub-id">urn:isbn:9990000000088</dc:identifier>
              <dc:title>Synthetic EPUB2 Book</dc:title>
              <dc:language>en</dc:language>
              <dc:creator>Test Author</dc:creator>
            </metadata>
            <manifest>
              <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
              <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
              <item id="ch2" href="ch2.xhtml" media-type="application/xhtml+xml"/>
            </manifest>
            <spine toc="ncx">
              <itemref idref="ch1"/>
              <itemref idref="ch2"/>
            </spine>
          </package>
          """;

    public static byte[] BuildEpub3() => Build(epub3: true);
    public static byte[] BuildEpub2() => Build(epub3: false);

    private static byte[] Build(bool epub3)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // mimetype MUST be first + stored (uncompressed) per EPUB OCF spec.
            var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var w = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
                w.Write("application/epub+zip");

            AddEntry(zip, "META-INF/container.xml", ContainerXml);
            AddEntry(zip, "OEBPS/content.opf", Opf(epub3));
            AddEntry(zip, "OEBPS/ch1.xhtml", Chapter1);
            AddEntry(zip, "OEBPS/ch2.xhtml", Chapter2);
            if (epub3) AddEntry(zip, "OEBPS/nav.xhtml", Nav);
            else AddEntry(zip, "OEBPS/toc.ncx", Ncx);
        }
        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
