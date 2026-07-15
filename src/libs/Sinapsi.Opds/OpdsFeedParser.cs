// ---------------------------------------------------------------------------
// OpdsFeedParser - pure OPDS 1.2 (Atom) XML -> OpdsFeed. No HTTP, no I/O.
//
// Split out from OpdsClient so the parse logic is unit-testable against authored
// fixture XML with zero network. OPDS 1.2 is Atom (RFC 4287) plus the OPDS
// namespace for acquisition rels and Dublin-Core for language/identifier; the
// feed itself carries navigation entries (links to sub-catalogs) and acquisition
// entries (downloadable publications) in the same <entry> shape.
//
// Uses System.Xml.Linq (BCL): OPDS is well-formed Atom XML from spec-conformant
// servers, so no HTML-tolerance is needed here (unlike EPUB xhtml, which does).
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Sinapsi.Opds.Models;

namespace Sinapsi.Opds;

/// <summary>Parses OPDS 1.2 Atom feed XML into <see cref="OpdsFeed"/>. Stateless;
/// all methods are static + side-effect-free.</summary>
public static class OpdsFeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";

    /// <summary>
    /// Parse an OPDS feed document. <paramref name="requestUri"/> is the URL the
    /// feed was fetched from, used to resolve relative link hrefs (incl. the
    /// <c>rel="next"</c> pagination link) to absolute URIs; pass null to leave
    /// hrefs as-is. Throws <see cref="System.Xml.XmlException"/> on malformed XML.
    /// </summary>
    public static OpdsFeed Parse(string xml, Uri? requestUri = null)
    {
        // DtdProcessing.Prohibit + no resolver: never fetch a DTD, never expand an
        // external entity (XXE-safe) - a catalog server is not fully trusted input.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
        };
        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, settings);
        var doc = XDocument.Load(reader);
        return ParseDocument(doc, requestUri);
    }

    private static OpdsFeed ParseDocument(XDocument doc, Uri? requestUri)
    {
        var feed = doc.Root;
        if (feed is null || feed.Name != Atom + "feed")
            throw new FormatException("Not an OPDS/Atom feed: root element is not <feed>.");

        var feedLinks = feed.Elements(Atom + "link").Select(ParseLink).ToList();
        var entries = feed.Elements(Atom + "entry").Select(e => ParseEntry(e, requestUri)).ToList();

        string? nextHref = ResolveNext(feedLinks, requestUri);

        return new OpdsFeed
        {
            Id = feed.Element(Atom + "id")?.Value.Trim(),
            Title = feed.Element(Atom + "title")?.Value.Trim(),
            Entries = entries,
            Links = ResolveLinks(feedLinks, requestUri),
            NextHref = nextHref,
        };
    }

    private static OpdsEntry ParseEntry(XElement e, Uri? requestUri)
    {
        var links = e.Elements(Atom + "link").Select(ParseLink).ToList();
        var resolved = ResolveLinks(links, requestUri);

        var authors = e.Elements(Atom + "author")
            .Select(a => a.Element(Atom + "name")?.Value.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        var categories = e.Elements(Atom + "category")
            .Select(c => (string?)c.Attribute("term") ?? c.Attribute("label")?.Value)
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!.Trim())
            .ToList();

        var language = (e.Element(DcTerms + "language") ?? e.Element(Dc + "language"))?.Value.Trim();
        var identifier = (e.Element(DcTerms + "identifier") ?? e.Element(Dc + "identifier"))?.Value.Trim();

        return new OpdsEntry
        {
            Id = e.Element(Atom + "id")?.Value.Trim() ?? string.Empty,
            Title = e.Element(Atom + "title")?.Value.Trim() ?? string.Empty,
            Authors = authors,
            Categories = categories,
            Updated = ParseDate(e.Element(Atom + "updated")?.Value),
            Language = string.IsNullOrEmpty(language) ? null : language,
            Identifier = string.IsNullOrEmpty(identifier) ? null : identifier,
            Summary = (e.Element(Atom + "summary")?.Value ?? e.Element(Atom + "content")?.Value)?.Trim(),
            AcquisitionLinks = resolved.Where(l => l.IsAcquisition).ToList(),
        };
    }

    private static OpdsLink ParseLink(XElement l) => new(
        Href: (string?)l.Attribute("href") ?? string.Empty,
        Type: (string?)l.Attribute("type"),
        Rel: (string?)l.Attribute("rel"),
        Title: (string?)l.Attribute("title"));

    private static IReadOnlyList<OpdsLink> ResolveLinks(IEnumerable<OpdsLink> links, Uri? baseUri)
    {
        if (baseUri is null) return links.ToList();
        return links.Select(l => l with { Href = ResolveHref(l.Href, baseUri) }).ToList();
    }

    private static string? ResolveNext(IEnumerable<OpdsLink> links, Uri? baseUri)
    {
        var next = links.FirstOrDefault(l => string.Equals(l.Rel, "next", StringComparison.OrdinalIgnoreCase));
        if (next is null || string.IsNullOrEmpty(next.Href)) return null;
        return ResolveHref(next.Href, baseUri);
    }

    /// <summary>Resolve a possibly-relative href against the feed request URI.
    /// Absolute hrefs pass through unchanged; a null base leaves the href as-is.</summary>
    private static string ResolveHref(string href, Uri? baseUri)
    {
        if (string.IsNullOrEmpty(href) || baseUri is null) return href;
        return Uri.TryCreate(baseUri, href, out var abs) ? abs.ToString() : href;
    }

    private static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(
            s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dt)
            ? dt
            : null;
    }
}
