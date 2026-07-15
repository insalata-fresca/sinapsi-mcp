// ---------------------------------------------------------------------------
// OPDS 1.2 (Atom) model types.
//
// These are the parsed, transport-neutral shape of an OPDS catalog: a feed is a
// navigation feed OR an acquisition feed (OPDS distinguishes them by the media
// type of the links / entries, not by a separate element), and each entry maps
// to an OpdsEntry with its acquisition links. A caller (M4's OpdsSourceScanner)
// enumerates entries, picks the application/epub+zip acquisition link, and can
// diff a fresh entry set against a prior {Id -> Updated} snapshot to find
// new / changed / removed books.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

namespace Sinapsi.Opds.Models;

/// <summary>
/// One acquisition/navigation link on an OPDS entry (or feed). <see cref="Rel"/>
/// is the Atom <c>rel</c> (e.g. <c>http://opds-spec.org/acquisition</c>,
/// <c>http://opds-spec.org/acquisition/open-access</c>, <c>subsection</c>,
/// <c>next</c>); <see cref="Type"/> is the MIME type (e.g.
/// <c>application/epub+zip</c>, <c>application/atom+xml;profile=opds-catalog</c>).
/// </summary>
public sealed record OpdsLink(string Href, string? Type, string? Rel, string? Title = null)
{
    /// <summary>The OPDS acquisition rel and its sub-rels (open-access, buy, borrow,
    /// sample, ...) all start with this. A plain catalog navigation link does not.</summary>
    public const string AcquisitionRelPrefix = "http://opds-spec.org/acquisition";

    /// <summary>MIME type for an EPUB acquisition resource.</summary>
    public const string EpubMediaType = "application/epub+zip";

    /// <summary>True if this link is any OPDS acquisition link (the entry is a
    /// downloadable publication, not a sub-catalog).</summary>
    public bool IsAcquisition =>
        Rel is not null && Rel.StartsWith(AcquisitionRelPrefix, StringComparison.Ordinal);

    /// <summary>True if this acquisition link points at an EPUB.</summary>
    public bool IsEpub =>
        Type is not null && Type.StartsWith(EpubMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Atom media-type prefix an OPDS catalog (navigation OR acquisition)
    /// sub-feed link carries: <c>application/atom+xml</c> (usually with a
    /// <c>;profile=opds-catalog</c> parameter). A link into a sub-catalog has this
    /// type; a publication acquisition link (epub/pdf) or a cover image does not.</summary>
    public const string CatalogMediaType = "application/atom+xml";

    /// <summary>Feed/entry <c>rel</c> values that are structural navigation of the
    /// CURRENT feed, NOT a link into a child sub-catalog. Following these is how a
    /// crawler loops back to the root (start/up) or self — the traversal must skip
    /// them (the cycle guard is a second line of defence, but skipping is cheaper).</summary>
    private static readonly HashSet<string> NonDescentRels = new(StringComparer.OrdinalIgnoreCase)
    {
        "self", "start", "up", "search", "first", "last", "previous", "prev", "next",
        // OPDS facet/sort/shelf/crawlable rels point sideways/at the same catalog plane,
        // not down into a distinct book sub-feed; dedup-by-Id makes following them safe,
        // but they are not descent edges.
        "http://opds-spec.org/sort/new", "http://opds-spec.org/sort/popular",
        "http://opds-spec.org/crawlable", "http://opds-spec.org/shelf",
        "http://opds-spec.org/subscriptions", "http://opds-spec.org/featured",
    };

    /// <summary>True when this link points DOWN into a child OPDS sub-catalog feed we
    /// should traverse: an <c>application/atom+xml</c> catalog link whose <c>rel</c> is
    /// NOT a structural/self/start/up/search/pagination rel. Covers BookLore/Calibre-Web's
    /// <c>rel="subsection"</c> nav entries and feed-level catalog links alike. A link with
    /// no <c>rel</c> but a catalog type still counts (some servers omit rel on nav entries).</summary>
    public bool IsNavigationDescent =>
        Type is not null
        && Type.StartsWith(CatalogMediaType, StringComparison.OrdinalIgnoreCase)
        && !IsAcquisition
        && (Rel is null || !NonDescentRels.Contains(Rel));
}

/// <summary>
/// A single OPDS catalog entry (Atom <c>&lt;entry&gt;</c>). For an acquisition
/// feed this is one publication; the EPUB bytes come from the
/// <c>application/epub+zip</c> link in <see cref="AcquisitionLinks"/>. All list
/// members are non-null (empty, never null) so a caller never has to null-check.
/// </summary>
public sealed record OpdsEntry
{
    /// <summary>Atom <c>&lt;id&gt;</c> - the stable identity used to key + diff entries.</summary>
    public required string Id { get; init; }

    /// <summary>Atom <c>&lt;title&gt;</c>.</summary>
    public required string Title { get; init; }

    /// <summary>Atom <c>&lt;author&gt;&lt;name&gt;</c> values.</summary>
    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

    /// <summary>Atom <c>&lt;category term="..."&gt;</c> values (subjects/BISAC/…).</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>Atom <c>&lt;updated&gt;</c> parsed to UTC, when present. This is
    /// the change token for the diff (an entry whose Updated advanced is "changed").</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Dublin-Core <c>&lt;dcterms:language&gt;</c> / <c>&lt;dc:language&gt;</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Dublin-Core identifier - an ISBN/URN when the server supplies one.</summary>
    public string? Identifier { get; init; }

    /// <summary>Atom <c>&lt;summary&gt;</c> / <c>&lt;content&gt;</c> text, if any.</summary>
    public string? Summary { get; init; }

    /// <summary>All acquisition links on the entry (EPUB, PDF, mobi, cover thumbnails
    /// are excluded - only <c>rel</c> starting with the acquisition prefix).</summary>
    public IReadOnlyList<OpdsLink> AcquisitionLinks { get; init; } = Array.Empty<OpdsLink>();

    /// <summary>Navigation-descent links on this entry: <c>application/atom+xml</c>
    /// catalog links into a child sub-feed (a BookLore/Calibre-Web nav entry — "All
    /// Books", an author, a series — carries exactly one). Empty for a publication
    /// (acquisition) entry. The traversal follows these to reach acquisition feeds.</summary>
    public IReadOnlyList<OpdsLink> NavigationLinks { get; init; } = Array.Empty<OpdsLink>();

    /// <summary>True if this entry has at least one acquisition link (it is a
    /// downloadable publication, not a pure navigation entry).</summary>
    public bool IsAcquisition => AcquisitionLinks.Count > 0;

    /// <summary>The best EPUB acquisition link on this entry, or null if none. Prefers
    /// a plain <c>.../acquisition</c> or open-access rel over sample/buy/borrow.</summary>
    public OpdsLink? EpubLink =>
        AcquisitionLinks.Where(l => l.IsEpub)
            .OrderBy(l => AcquisitionRelPriority(l.Rel))
            .FirstOrDefault();

    private static int AcquisitionRelPriority(string? rel) => rel switch
    {
        "http://opds-spec.org/acquisition" => 0,
        "http://opds-spec.org/acquisition/open-access" => 1,
        "http://opds-spec.org/acquisition/borrow" => 2,
        _ when rel is not null && rel.Contains("sample", StringComparison.Ordinal) => 9,
        _ => 5,
    };
}

/// <summary>
/// One parsed OPDS feed page: its own identity + title, the entries on this page,
/// and the pagination links. <see cref="NextHref"/> is the resolved absolute URL
/// of the <c>rel="next"</c> link when present - the client follows it to page
/// through an acquisition feed.
/// </summary>
public sealed record OpdsFeed
{
    /// <summary>Atom feed <c>&lt;id&gt;</c>.</summary>
    public string? Id { get; init; }

    /// <summary>Atom feed <c>&lt;title&gt;</c>.</summary>
    public string? Title { get; init; }

    /// <summary>The entries on THIS page (not across pagination).</summary>
    public IReadOnlyList<OpdsEntry> Entries { get; init; } = Array.Empty<OpdsEntry>();

    /// <summary>Feed-level links (self/start/up/next/subsection/...).</summary>
    public IReadOnlyList<OpdsLink> Links { get; init; } = Array.Empty<OpdsLink>();

    /// <summary>Absolute href of the <c>rel="next"</c> pagination link, resolved
    /// against the feed's request URI. Null when this is the last (or only) page.</summary>
    public string? NextHref { get; init; }

    /// <summary>True if this page is a navigation feed - every entry links only to
    /// sub-catalogs (no acquisition links). Informational; callers usually just
    /// enumerate <see cref="Entries"/> and inspect each entry's acquisition links.</summary>
    public bool IsNavigation => Entries.Count > 0 && Entries.All(e => e.AcquisitionLinks.Count == 0);

    /// <summary>Feed-LEVEL navigation-descent links (resolved absolute hrefs): the
    /// <c>application/atom+xml</c> catalog links in <see cref="Links"/> that point down
    /// into a child sub-feed (not self/start/up/search/pagination). Some servers expose
    /// sections as feed-level links rather than nav entries; the traversal follows both.</summary>
    public IReadOnlyList<OpdsLink> NavigationLinks =>
        Links.Where(l => l.IsNavigationDescent).ToList();
}

/// <summary>
/// The result of diffing a fresh OPDS entry set against a prior snapshot of
/// <c>{Id -> change-token}</c>. New = present now, absent before; Changed = present
/// in both but the change-token advanced; Removed = present before, absent now
/// (tombstone candidates). Side-effect-free value type.
/// </summary>
public sealed record OpdsDiff(
    IReadOnlyList<OpdsEntry> New,
    IReadOnlyList<OpdsEntry> Changed,
    IReadOnlyList<string> Removed)
{
    /// <summary>True if nothing new/changed/removed.</summary>
    public bool IsEmpty => New.Count == 0 && Changed.Count == 0 && Removed.Count == 0;
}
