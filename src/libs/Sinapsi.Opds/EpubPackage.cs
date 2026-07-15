// ---------------------------------------------------------------------------
// EpubPackage - parses the STRUCTURAL EPUB files (container.xml, the OPF, and
// the EPUB3 nav.xhtml / EPUB2 toc.ncx table of contents). These are well-formed
// XML per the EPUB spec, so System.Xml.Linq (BCL) is used - unlike the CONTENT
// xhtml (see EpubExtractor), which is real-world HTML and needs HtmlAgilityPack.
//
// Kept separate from EpubExtractor so the OPF/spine/toc parse is independently
// unit-testable and the extractor stays focused on xhtml -> clean text.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Xml;
using System.Xml.Linq;

namespace Sinapsi.Opds;

/// <summary>A manifest item: its OPF href (relative to the OPF file) + media type.</summary>
internal sealed record ManifestItem(string Id, string Href, string? MediaType, bool IsNav);

/// <summary>The parsed OPF package: metadata + spine (as ordered content hrefs)
/// + the TOC document href (nav.xhtml or toc.ncx), all resolved relative to the
/// EPUB zip root.</summary>
internal sealed record EpubPackage(
    string? Title,
    IReadOnlyList<string> Authors,
    string? Language,
    string? Identifier,
    IReadOnlyList<string> SpineHrefs,
    string? TocHref,
    bool TocIsNav);

/// <summary>Parses container.xml, the OPF, and the nav/ncx TOC.</summary>
internal static class EpubPackageParser
{
    private static readonly XNamespace Ocf = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    private static XDocument LoadXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
        };
        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, settings);
        return XDocument.Load(reader);
    }

    /// <summary>Resolve the OPF file path from META-INF/container.xml.</summary>
    public static string ParseContainerOpfPath(string containerXml)
    {
        var doc = LoadXml(containerXml);
        var rootfile = doc.Descendants(Ocf + "rootfile").FirstOrDefault()
            ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
        var path = (string?)rootfile?.Attribute("full-path");
        if (string.IsNullOrEmpty(path))
            throw new FormatException("container.xml has no rootfile/@full-path (not a valid EPUB).");
        return path;
    }

    /// <summary>Parse the OPF into metadata + spine hrefs (zip-root-relative) +
    /// the TOC document href. <paramref name="opfDir"/> is the directory of the
    /// OPF inside the zip (e.g. "OEBPS/"), used to resolve manifest hrefs.</summary>
    public static EpubPackage ParseOpf(string opfXml, string opfDir)
    {
        var doc = LoadXml(opfXml);
        var pkg = doc.Root ?? throw new FormatException("OPF has no root element.");

        // Namespace-tolerant: some OPFs omit / vary the OPF namespace.
        XElement? metadata = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        XElement? manifest = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "manifest");
        XElement? spine = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "spine");

        var title = MetaValue(metadata, "title");
        var language = MetaValue(metadata, "language");
        var identifier = MetaValue(metadata, "identifier");
        var authors = metadata?.Elements().Where(e => e.Name.LocalName == "creator")
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList()
            ?? new List<string>();

        var items = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        if (manifest is not null)
        {
            foreach (var item in manifest.Elements().Where(e => e.Name.LocalName == "item"))
            {
                var id = (string?)item.Attribute("id");
                var href = (string?)item.Attribute("href");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(href)) continue;
                var props = (string?)item.Attribute("properties") ?? string.Empty;
                var isNav = props.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(p => string.Equals(p, "nav", StringComparison.Ordinal));
                items[id] = new ManifestItem(id, href, (string?)item.Attribute("media-type"), isNav);
            }
        }

        // Spine order -> content hrefs (resolved to zip-root-relative).
        var spineHrefs = new List<string>();
        if (spine is not null)
        {
            foreach (var itemref in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
            {
                var idref = (string?)itemref.Attribute("idref");
                var linear = (string?)itemref.Attribute("linear");
                if (string.Equals(linear, "no", StringComparison.OrdinalIgnoreCase)) continue;
                if (idref is not null && items.TryGetValue(idref, out var mi))
                    spineHrefs.Add(ZipPath.Combine(opfDir, mi.Href));
            }
        }

        // TOC: EPUB3 nav (manifest item with properties="nav") preferred; else
        // EPUB2 ncx (spine/@toc idref, or the first ncx-typed manifest item).
        string? tocHref = null;
        bool tocIsNav = false;
        var navItem = items.Values.FirstOrDefault(i => i.IsNav);
        if (navItem is not null)
        {
            tocHref = ZipPath.Combine(opfDir, navItem.Href);
            tocIsNav = true;
        }
        else
        {
            var tocId = (string?)spine?.Attribute("toc");
            ManifestItem? ncx = null;
            if (tocId is not null) items.TryGetValue(tocId, out ncx);
            ncx ??= items.Values.FirstOrDefault(i =>
                string.Equals(i.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase));
            if (ncx is not null)
            {
                tocHref = ZipPath.Combine(opfDir, ncx.Href);
                tocIsNav = false;
            }
        }

        return new EpubPackage(
            Title: title,
            Authors: authors,
            Language: language,
            Identifier: identifier,
            SpineHrefs: spineHrefs,
            TocHref: tocHref,
            TocIsNav: tocIsNav);
    }

    private static string? MetaValue(XElement? metadata, string localName)
    {
        var v = metadata?.Elements()
            .FirstOrDefault(e => e.Name.LocalName == localName && e.Name.Namespace == Dc)?.Value
            ?? metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
        v = v?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>
    /// Parse the TOC into a map of {content-href (zip-root-relative, fragment
    /// stripped) -&gt; chapter title}. Handles EPUB3 nav.xhtml (nav[epub:type=toc]
    /// &gt; ol &gt; li &gt; a) and EPUB2 toc.ncx (navMap &gt; navPoint &gt;
    /// navLabel/text + content/@src). Best-effort: returns an empty map on any
    /// parse trouble (the extractor then falls back to per-document headings).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseToc(string tocXml, string tocDir, bool isNav)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = LoadXml(tocXml);
            if (isNav)
            {
                var nav = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav"
                    && (e.Attributes().Any(a => a.Name.LocalName == "type" && a.Value.Contains("toc"))
                        || (string?)e.Attribute("id") == "toc"))
                    ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav");
                if (nav is not null)
                {
                    foreach (var a in nav.Descendants().Where(e => e.Name.LocalName == "a"))
                    {
                        var href = (string?)a.Attribute("href");
                        var text = NormalizeWhitespace(a.Value);
                        AddToc(map, tocDir, href, text);
                    }
                }
            }
            else
            {
                foreach (var np in doc.Descendants().Where(e => e.Name.LocalName == "navPoint"))
                {
                    var label = np.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")
                        ?.Elements().FirstOrDefault(e => e.Name.LocalName == "text")?.Value;
                    var src = np.Elements().FirstOrDefault(e => e.Name.LocalName == "content")
                        ?.Attribute("src")?.Value;
                    AddToc(map, tocDir, src, NormalizeWhitespace(label ?? string.Empty));
                }
            }
        }
        catch
        {
            // Best-effort TOC: fall through to whatever we collected.
        }
        return map;
    }

    private static void AddToc(Dictionary<string, string> map, string tocDir, string? href, string text)
    {
        if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(text)) return;
        var noFrag = href.Split('#', 2)[0];
        if (noFrag.Length == 0) return;
        var full = ZipPath.Combine(tocDir, noFrag);
        // First TOC entry for a document wins (top-level chapter title).
        if (!map.ContainsKey(full)) map[full] = text;
    }

    private static string NormalizeWhitespace(string s)
    {
        var parts = s.Split(new[] { ' ', '\t', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).Trim();
    }
}

/// <summary>Zip-entry path helpers (always '/'-separated, no "./" or "../"),
/// so a manifest href relative to the OPF dir resolves to a stable zip entry
/// name that matches <see cref="System.IO.Compression.ZipArchive"/> entries.</summary>
internal static class ZipPath
{
    public static string Combine(string dir, string relative)
    {
        // Absolute-from-root href (rare) - strip leading slash, ignore dir.
        if (relative.StartsWith('/')) return Normalize(relative.TrimStart('/'));
        var baseParts = dir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var relParts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in relParts)
        {
            if (p == ".") continue;
            if (p == "..") { if (baseParts.Count > 0) baseParts.RemoveAt(baseParts.Count - 1); }
            else baseParts.Add(p);
        }
        return string.Join('/', baseParts);
    }

    public static string DirOf(string zipPath)
    {
        var idx = zipPath.Replace('\\', '/').LastIndexOf('/');
        return idx < 0 ? string.Empty : zipPath[..idx];
    }

    private static string Normalize(string p)
    {
        var parts = p.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x != ".");
        return string.Join('/', parts);
    }
}
