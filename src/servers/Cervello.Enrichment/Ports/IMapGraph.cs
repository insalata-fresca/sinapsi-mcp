using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port over the on-CT map GRAPH — the <c>map/{people,threads,goals}/&lt;slug&gt;.md</c> dossiers +
/// <c>map/timeline.md</c> in the cervello working tree. This is the graph half of the "graph+index
/// backed" pack assembler (design §2.1/§2.5): it reads objects verbatim (<c>cervello_get</c>, §5.3),
/// walks dated movement (<c>cervello_timeline_walk</c>, §5.4), and yields 1-hop neighbours for the
/// pack's graph-proximity ranking + neighbour sections (goal_reasoning/person_prep).
///
/// <para>The live adapter (<see cref="Adapters.RepoMapGraph"/>) reads the CT-local working tree; a
/// fake stands in for tests (no filesystem, no personal data). All reads are verbatim — no LLM, no
/// invention (the never-guess floor); a missing object → null, never a fabricated dossier.</para>
/// </summary>
public interface IMapGraph
{
    /// <summary>Read one map object verbatim (frontmatter + body + cited refs). Null if it does not exist.</summary>
    Task<MapObject?> GetObjectAsync(MapObjectKind kind, string slug, CancellationToken ct = default);

    /// <summary>
    /// Walk the dated, sourced movement lines for an anchor over an optional date range, newest first.
    /// <paramref name="anchor"/> is <c>goal:&lt;slug&gt; | person:&lt;slug&gt; | thread:&lt;slug&gt; | global</c>.
    /// A person/thread anchor draws from the global timeline lines that link the entity; a goal anchor
    /// reads the goal's <c>## Movimento</c>. Lines without a source ref (LINT R1) are dropped.
    /// </summary>
    Task<IReadOnlyList<TimelineLine>> WalkTimelineAsync(string anchor, string? from, string? to, CancellationToken ct = default);

    /// <summary>The slugs of the object's 1-hop graph neighbours (people/threads/goals it links to).</summary>
    Task<IReadOnlyList<GraphNeighbour>> NeighboursAsync(MapObjectKind kind, string slug, CancellationToken ct = default);

    /// <summary>List every goal dossier slug (for the <c>portfolio</c> sweep). Empty if none.</summary>
    Task<IReadOnlyList<string>> ListGoalSlugsAsync(CancellationToken ct = default);
}

/// <summary>A 1-hop neighbour of a map object (its kind + slug), for graph-proximity ranking + neighbour sections.</summary>
public sealed record GraphNeighbour(MapObjectKind Kind, string Slug);
