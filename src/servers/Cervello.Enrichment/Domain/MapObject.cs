namespace Cervello.Enrichment.Domain;

/// <summary>
/// The kind of a map object (SCHEMAS §2/§3 + the net-new §3.1 <c>goal</c>). <c>Goal</c> is the
/// net-new dossier type (design §3.1, MC-ratified Q1) living at <c>map/goals/&lt;slug&gt;.md</c>,
/// mirroring the person/thread dossier so lint + the graph-writer + the pack assembler treat it
/// like any other dossier. <c>Timeline</c> is the global <c>map/timeline.md</c>.
/// </summary>
public enum MapObjectKind
{
    Person,
    Thread,
    Goal,
    Timeline,
}

/// <summary>
/// One map object read verbatim from the CT working tree (design §5.3 <c>cervello_get</c>): its kind,
/// slug, parsed frontmatter (flat key → string, tolerant), the raw markdown body, and the resolved
/// source refs it cites. A READ model — the on-CT resolver produces it; the API renders it.
/// </summary>
public sealed record MapObject(
    MapObjectKind Kind,
    string Id,
    IReadOnlyDictionary<string, string> Frontmatter,
    string BodyMarkdown,
    IReadOnlyList<string> Sources);

/// <summary>
/// One dated, sourced movement line (SCHEMAS §4 grammar:
/// <c>- YYYY-MM-DD — &lt;fact&gt; — [[link]]… — source: &lt;ref&gt;[ &lt;ref&gt;…]</c>). The read
/// model behind <c>cervello_timeline_walk</c> (design §5.4) and the goal <c>## Movimento</c>
/// evidence timeline (§3.1). Every line carries ≥1 source (LINT R1) — a line without one is not a
/// valid movement line and is dropped by the parser.
/// </summary>
public sealed record TimelineLine(string Date, string Fact, IReadOnlyList<string> Links, string Source);
