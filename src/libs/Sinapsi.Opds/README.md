# Sinapsi.Opds

A small .NET **library** with two cohesive, standalone components for turning an
**OPDS** book catalog into clean, structured book text:

1. **`OpdsClient`** — a generic **OPDS 1.2** (Atom) catalog client for *any* OPDS
   server (BookLore, Calibre-Web, Kavita, Komga, …), not tied to one product.
2. **`EpubExtractor`** — an EPUB → structured-text extractor.

It is consumed by services via `PackageReference`, not run as a server. Part of a
personal research lab; offered as-is.

## `OpdsClient`

- Fetch + parse OPDS feeds — **navigation** feeds and **acquisition** feeds — and
  follow `link rel="next"` **pagination** (`EnumerateEntriesAsync` /
  `EnumerateAllAsync`).
- Each entry → an `OpdsEntry { Id, Title, Authors[], Categories[], Updated,
  Language?, Identifier?, AcquisitionLinks[] }` with an `EpubLink` helper that
  picks the `application/epub+zip` acquisition link.
- **Download** an acquisition link's bytes (`DownloadAsync`), size-capped.
- **Auth: HTTP Basic** or **anonymous**, injected via `OpdsClientOptions`
  (never hardcoded). `BuildBasicAuthHeader()` exposes the exact header for tests.
- **Diff** a fresh entry set against a prior `{Id → change-token}` snapshot
  (`OpdsClient.Diff` + `OpdsClient.Snapshot`) → `{New, Changed, Removed}` for
  incremental sync. Idempotent, side-effect-free (only HTTP GETs).

`OpdsFeedParser` is the pure XML→`OpdsFeed` parser (no I/O), independently testable.

## `EpubExtractor`

EPUB bytes/stream → `ExtractedBook { Title?, Authors[], Language?, Identifier?,
Chapters[] }`, where each `ExtractedChapter { Order, Heading, Href, Sections[] }`
and each `ExtractedSection { Heading?, HeadingLevel, Text }`.

- zip → `META-INF/container.xml` → `.opf` → **spine order** → per-spine-item xhtml.
- Clean text + **heading hierarchy** — sections split at `<h1..h6>`; chapter
  titles from the **nav/toc** (EPUB3 `nav.xhtml` or EPUB2 `toc.ncx`), falling back
  to the first heading / `<title>`.
- Strips `<script>`/`<style>`/`<head>`/`<svg>`, decodes entities, preserves
  paragraph + heading boundaries.

The **content xhtml** is parsed with **HtmlAgilityPack** (real EPUB xhtml carries
`&nbsp;` and, in EPUB2, unclosed void tags — both make the BCL XML parser throw).
The **structural** EPUB/OPDS XML (container/OPF/nav/ncx, Atom feeds) uses the BCL
`System.Xml.Linq` parser, since those are spec-well-formed XML. That is the only
external dependency.

## Composing them (the downstream scanner)

```csharp
var client = new OpdsClient(httpClient, new OpdsClientOptions { Username = u, Password = p });
// EnumerateAllAsync TRAVERSES navigation feeds: real OPDS roots (BookLore,
// Calibre-Web, Kavita, Komga) are NAVIGATION feeds whose entries are catalog links
// (All Books / Authors / Series), not books. The client descends nav sub-feeds +
// pages rel="next" at every level, and returns the full set of ACQUISITION entries
// de-duplicated by Id. Bounded fail-safe: cycle guard (never revisit a feed URL),
// MaxNavigationDepth (6), and OpdsClientOptions.MaxPages as the max-feeds budget.
var entries = await client.EnumerateAllAsync(feedUrl, ct);        // traverses nav + follows pagination
var diff    = OpdsClient.Diff(previousSnapshot, entries);         // new / changed / removed
foreach (var e in diff.New.Concat(diff.Changed))
    if (e.EpubLink is { } link)
    {
        var bytes = await client.DownloadAsync(link, ct);
        ExtractedBook book = EpubExtractor.Extract(bytes);        // -> chunk + embed
    }
var nextSnapshot = OpdsClient.Snapshot(entries);
```
