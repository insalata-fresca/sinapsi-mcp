// ---------------------------------------------------------------------------
// EpubExtractor - EPUB bytes/stream -> ExtractedBook (clean structured text).
//
// zip -> META-INF/container.xml -> OPF (manifest+spine) -> per-spine xhtml ->
// clean text + heading hierarchy. Chapter titles come from the nav/toc
// (EPUB3 nav.xhtml or EPUB2 toc.ncx) when available, else the document's first
// heading / <title>. Each xhtml document is split into sections at heading
// (<h1..h6>) boundaries so M3's chunker keeps book -> chapter -> section.
//
// The CONTENT xhtml is parsed with HtmlAgilityPack, NOT System.Xml.Linq: real
// EPUB xhtml carries named HTML entities (&nbsp; etc.) and, in EPUB2, unclosed
// void tags - both of which make XDocument throw ("Reference to undeclared
// entity 'nbsp'" / start-tag mismatch on <br>). The STRUCTURAL files
// (container/OPF/nav/ncx) stay on the BCL XML parser (see EpubPackage) because
// they are spec-well-formed XML. This is the sole reason HtmlAgilityPack is a
// dependency.
//
// Scripts + styles are dropped; entities decoded; paragraph + heading boundaries
// preserved (blank-line separated). Side-effect-free; reads the stream, no disk.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.IO.Compression;
using System.Text;
using HtmlAgilityPack;
using Sinapsi.Opds.Models;

namespace Sinapsi.Opds;

/// <summary>Extracts clean, structured text from an EPUB. Stateless; all methods static.</summary>
public static class EpubExtractor
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Extract from EPUB bytes (as returned by <c>OpdsClient.DownloadAsync</c>).</summary>
    public static ExtractedBook Extract(byte[] epubBytes)
    {
        ArgumentNullException.ThrowIfNull(epubBytes);
        using var ms = new MemoryStream(epubBytes, writable: false);
        return Extract(ms);
    }

    /// <summary>
    /// Extract from an EPUB stream. The stream must be seekable (a zip is read
    /// with random access); callers holding a network stream should buffer to a
    /// MemoryStream first (the byte[] overload does this).
    /// </summary>
    public static ExtractedBook Extract(Stream epubStream)
    {
        ArgumentNullException.ThrowIfNull(epubStream);
        using var zip = new ZipArchive(epubStream, ZipArchiveMode.Read, leaveOpen: true);

        var containerXml = ReadEntryText(zip, "META-INF/container.xml")
            ?? throw new FormatException("EPUB has no META-INF/container.xml.");
        var opfPath = EpubPackageParser.ParseContainerOpfPath(containerXml);
        var opfXml = ReadEntryText(zip, opfPath)
            ?? throw new FormatException($"EPUB OPF not found at '{opfPath}'.");
        var opfDir = ZipPath.DirOf(opfPath);
        var pkg = EpubPackageParser.ParseOpf(opfXml, opfDir);

        // TOC (best-effort) -> {content-href -> chapter title}.
        IReadOnlyDictionary<string, string> toc = new Dictionary<string, string>();
        if (pkg.TocHref is not null)
        {
            var tocXml = ReadEntryText(zip, pkg.TocHref);
            if (tocXml is not null)
                toc = EpubPackageParser.ParseToc(tocXml, ZipPath.DirOf(pkg.TocHref), pkg.TocIsNav);
        }

        var chapters = new List<ExtractedChapter>();
        int order = 0;
        foreach (var href in pkg.SpineHrefs)
        {
            var xhtml = ReadEntryText(zip, href);
            if (xhtml is null) { order++; continue; }

            var (docTitle, sections) = ExtractDocument(xhtml);
            // Chapter heading precedence: TOC title -> first heading -> <title>.
            var tocTitle = toc.TryGetValue(href, out var t) ? t : null;
            var firstHeading = sections.FirstOrDefault(s => s.Heading is not null)?.Heading;
            var heading = tocTitle ?? firstHeading ?? docTitle;

            // Skip a spine item that yielded no text at all (cover image page, etc.)?
            // Keep it - an empty chapter still marks spine position; but drop sections
            // that are entirely empty to keep the model clean.
            var nonEmpty = sections.Where(s => s.Text.Length > 0 || s.Heading is not null).ToList();
            chapters.Add(new ExtractedChapter(order, heading, href, nonEmpty));
            order++;
        }

        return new ExtractedBook
        {
            Title = pkg.Title,
            Authors = pkg.Authors,
            Language = pkg.Language,
            Identifier = pkg.Identifier,
            Chapters = chapters,
        };
    }

    /// <summary>
    /// Public helper: clean one xhtml document into (title, heading-split sections).
    /// Exposed so the xhtml->text logic is directly unit-testable without a zip.
    /// </summary>
    public static (string? Title, IReadOnlyList<ExtractedSection> Sections) ExtractDocument(string xhtml)
    {
        var htmlDoc = new HtmlDocument
        {
            // Decode &nbsp; etc.; keep it lenient (real EPUB content, not strict XML).
            OptionDefaultStreamEncoding = Encoding.UTF8,
        };
        htmlDoc.LoadHtml(xhtml);

        // Read the <title> BEFORE stripping <head> (it lives inside head).
        var title = htmlDoc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        title = string.IsNullOrWhiteSpace(title) ? null : NormalizeInline(title);

        // Strip non-content nodes outright.
        RemoveNodes(htmlDoc, "//script");
        RemoveNodes(htmlDoc, "//style");
        RemoveNodes(htmlDoc, "//head");
        RemoveNodes(htmlDoc, "//*[name()='svg']");

        var body = htmlDoc.DocumentNode.SelectSingleNode("//body")
            ?? htmlDoc.DocumentNode;

        // Walk the body in document order, emitting one text block per
        // block-level element and starting a new section at each heading.
        var sections = new List<ExtractedSection>();
        var current = new SectionBuilder(heading: null, level: 0);

        foreach (var node in EnumerateBlocks(body))
        {
            if (IsHeading(node, out int level))
            {
                var headingText = NormalizeInline(node.InnerText);
                if (string.IsNullOrEmpty(headingText)) continue;
                // Close the current section, open a new one at this heading.
                if (!current.IsEmpty) sections.Add(current.Build());
                current = new SectionBuilder(headingText, level);
            }
            else
            {
                var text = BlockText(node);
                if (text.Length > 0) current.AddParagraph(text);
            }
        }
        if (!current.IsEmpty) sections.Add(current.Build());

        return (title, sections);
    }

    // --- block walking ------------------------------------------------------

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p","div","section","article","li","blockquote","pre","figcaption","td","th","dd","dt",
        "h1","h2","h3","h4","h5","h6",
    };

    /// <summary>Yield the block-level content nodes of the body in document order.
    /// A block that contains nested blocks is NOT emitted itself (its children are),
    /// so text isn't duplicated; a block with only inline children is emitted whole.</summary>
    private static IEnumerable<HtmlNode> EnumerateBlocks(HtmlNode root)
    {
        foreach (var node in root.ChildNodes)
        {
            if (node.NodeType != HtmlNodeType.Element)
            {
                // Loose text directly under body -> wrap as an implicit paragraph.
                if (node.NodeType == HtmlNodeType.Text && node.InnerText.Trim().Length > 0)
                    yield return node;
                continue;
            }

            if (IsHeading(node, out _))
            {
                yield return node;
                continue;
            }

            if (BlockTags.Contains(node.Name))
            {
                if (ContainsNestedBlock(node))
                {
                    foreach (var child in EnumerateBlocks(node))
                        yield return child;
                }
                else
                {
                    yield return node;
                }
            }
            else
            {
                // Non-block wrapper (span/a/em at top level, or unknown) - recurse
                // so headings/blocks nested inside it are still found.
                if (ContainsNestedBlock(node))
                    foreach (var child in EnumerateBlocks(node))
                        yield return child;
                else if (node.InnerText.Trim().Length > 0)
                    yield return node;
            }
        }
    }

    private static bool ContainsNestedBlock(HtmlNode node) =>
        node.Descendants().Any(d => d.NodeType == HtmlNodeType.Element && BlockTags.Contains(d.Name));

    private static bool IsHeading(HtmlNode node, out int level)
    {
        level = 0;
        if (node.NodeType != HtmlNodeType.Element) return false;
        var n = node.Name;
        if (n.Length == 2 && (n[0] == 'h' || n[0] == 'H') && n[1] >= '1' && n[1] <= '6')
        {
            level = n[1] - '0';
            return true;
        }
        return false;
    }

    private static string BlockText(HtmlNode node) => NormalizeInline(node.InnerText);

    /// <summary>Decode entities + collapse inline whitespace to single spaces,
    /// trimmed. HtmlAgilityPack's InnerText already strips tags; we just clean it.</summary>
    private static string NormalizeInline(string raw)
    {
        var decoded = HtmlEntity.DeEntitize(raw) ?? raw;
        var sb = new StringBuilder(decoded.Length);
        bool prevSpace = false;
        foreach (var ch in decoded)
        {
            if (char.IsWhiteSpace(ch) || ch == ' ')
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else { sb.Append(ch); prevSpace = false; }
        }
        return sb.ToString().Trim();
    }

    private static void RemoveNodes(HtmlDocument doc, string xpath)
    {
        var nodes = doc.DocumentNode.SelectNodes(xpath);
        if (nodes is null) return;
        foreach (var n in nodes.ToList()) n.Remove();
    }

    private static string? ReadEntryText(ZipArchive zip, string entryPath)
    {
        // Zip entry names are '/'-separated and case-sensitive; try exact, then a
        // case-insensitive fallback (some producers vary case).
        var entry = zip.GetEntry(entryPath)
            ?? zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        return DecodeText(bytes);
    }

    /// <summary>Decode entry bytes to text honoring a UTF-8/UTF-16 BOM, defaulting
    /// to UTF-8 (the EPUB spec default).</summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Utf8NoBom.GetString(bytes);
    }

    /// <summary>Accumulates paragraphs into one section, joined with blank lines.</summary>
    private sealed class SectionBuilder
    {
        private readonly string? _heading;
        private readonly int _level;
        private readonly List<string> _paragraphs = new();

        public SectionBuilder(string? heading, int level)
        {
            _heading = heading;
            _level = level;
        }

        public bool IsEmpty => _heading is null && _paragraphs.Count == 0;

        public void AddParagraph(string text)
        {
            if (text.Length > 0) _paragraphs.Add(text);
        }

        public ExtractedSection Build() =>
            new(_heading, _level, string.Join("\n\n", _paragraphs));
    }
}
