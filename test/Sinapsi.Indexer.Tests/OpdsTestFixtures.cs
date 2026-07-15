// ---------------------------------------------------------------------------
// OpdsTestFixtures - synthetic OPDS feed XML + synthetic EPUB zips + a canned
// HttpMessageHandler, for OpdsSourceScanner tests. NO licensed content, NO
// network: everything is generated in-memory. Kept in THIS test assembly (a
// near-copy of the Opds.Tests helpers, which are `internal` to that assembly)
// so the scanner can be exercised end-to-end (feed -> EPUB -> chunk -> Document)
// entirely hermetically.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.IO.Compression;
using System.Net;
using System.Text;

namespace Sinapsi.Indexer.Tests;

/// <summary>Synthetic OPDS acquisition feeds + synthetic EPUBs for the scanner tests.</summary>
internal static class OpdsTestFixtures
{
    public const string FeedUrl = "http://library.test:6060/opds/books";

    /// <summary>An acquisition feed with the two given entry ids, each with an
    /// EPUB acquisition link at <c>/dl/&lt;dlName&gt;.epub</c> and the given
    /// &lt;updated&gt; change-token. Any of the entries may be omitted (null) to
    /// build a "removed an entry" feed.</summary>
    public static string AcqFeed(params (string Id, string Title, string Updated, string DlName)[] entries)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom"
                  xmlns:dcterms="http://purl.org/dc/terms/">
              <id>urn:test:books</id>
              <title>All Books</title>
              <link rel="self" href="/opds/books" type="application/atom+xml"/>
            """);
        foreach (var (id, title, updated, dl) in entries)
        {
            sb.Append($"""
                  <entry>
                    <id>{id}</id>
                    <title>{title}</title>
                    <author><name>Ada Testwriter</name></author>
                    <category term="Software Engineering"/>
                    <dcterms:identifier>{id}</dcterms:identifier>
                    <updated>{updated}</updated>
                    <link rel="http://opds-spec.org/acquisition" href="/dl/{dl}.epub" type="application/epub+zip"/>
                  </entry>
                """);
        }
        sb.Append("</feed>");
        return sb.ToString();
    }

    // --- synthetic EPUB ------------------------------------------------------

    private const string ContainerXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static string Chapter(string heading, string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>{heading}</title></head>
        <body>
          <h1>{heading}</h1>
          <p>{body}</p>
        </body>
        </html>
        """;

    private static string Opf(string title, string isbn) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="pub-id">{isbn}</dc:identifier>
            <dc:title>{title}</dc:title>
            <dc:language>en</dc:language>
            <dc:creator>OPF Author</dc:creator>
          </metadata>
          <manifest>
            <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
          </manifest>
          <spine>
            <itemref idref="ch1"/>
          </spine>
        </package>
        """;

    /// <summary>A minimal valid single-chapter EPUB with the given metadata + a
    /// paragraph long enough to yield at least one chunk.</summary>
    public static byte[] BuildEpub(string title, string isbn, string heading, string body)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var w = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
                w.Write("application/epub+zip");
            AddEntry(zip, "META-INF/container.xml", ContainerXml);
            AddEntry(zip, "OEBPS/content.opf", Opf(title, isbn));
            AddEntry(zip, "OEBPS/ch1.xhtml", Chapter(heading, body));
        }
        return ms.ToArray();
    }

    /// <summary>A default EPUB body with plenty of words so the chunker emits >=1 chunk.</summary>
    public static string LongBody(string seed) =>
        string.Join(' ', Enumerable.Range(0, 400).Select(i => $"{seed}word{i}"));

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    // --- canned handler ------------------------------------------------------

    /// <summary>
    /// A canned <see cref="HttpMessageHandler"/>: serves a feed XML for the feed
    /// path and per-download EPUB bytes keyed by the request path's last segment
    /// (e.g. "/dl/a.epub" -> the bytes registered under "a"). A download whose
    /// key is registered as a THROW simulates a poison entry (404). Records the
    /// download paths it served so a test can assert re-downloads were / were not
    /// made.
    /// </summary>
    internal sealed class CannedOpdsHandler : HttpMessageHandler
    {
        private readonly Func<string> _feedProvider;
        private readonly Dictionary<string, byte[]> _epubs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _poison = new(StringComparer.Ordinal);

        /// <summary>Every download path (last URI segment sans ".epub") served, in order.</summary>
        public List<string> Downloaded { get; } = new();

        public CannedOpdsHandler(Func<string> feedProvider) => _feedProvider = feedProvider;

        public CannedOpdsHandler Epub(string key, byte[] bytes) { _epubs[key] = bytes; return this; }
        public CannedOpdsHandler Poison(string key) { _poison.Add(key); return this; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/opds/", StringComparison.Ordinal) || path.EndsWith("/books", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_feedProvider(), Encoding.UTF8, "application/atom+xml"),
                });
            }

            // A download: last segment "a.epub" -> key "a".
            var last = path.Split('/').Last();
            var key = last.EndsWith(".epub", StringComparison.Ordinal) ? last[..^5] : last;
            Downloaded.Add(key);

            if (_poison.Contains(key))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (_epubs.TryGetValue(key, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
