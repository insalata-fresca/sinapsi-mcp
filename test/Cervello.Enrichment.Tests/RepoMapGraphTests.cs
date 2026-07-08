using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// <see cref="RepoMapGraph"/> — the on-CT map reader (design §2/§5.3/§5.4). Proves verbatim reads of a
/// SYNTHETIC working tree (no personal data): frontmatter parse, SCHEMAS §4 movement-line parse (with
/// the mandatory <c>source:</c> — LINT R1), neighbour traversal, and the never-guess floor (a missing
/// dossier → null). Written to a temp dir, torn down after.
/// </summary>
public sealed class RepoMapGraphTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cervello-mapgraph-" + Guid.NewGuid().ToString("N"));

    public RepoMapGraphTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "map", "goals"));
        Directory.CreateDirectory(Path.Combine(_root, "map", "people"));
        Directory.CreateDirectory(Path.Combine(_root, "map", "threads"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private void Write(string rel, string content) => File.WriteAllText(Path.Combine(_root, rel), content.Replace("\r\n", "\n"));

    [Fact]
    public async Task Get_object_parses_frontmatter_body_and_sources()
    {
        Write("map/goals/series-a.md", """
            ---
            type: goal
            name: Raise Series A
            status: active
            people: [guilhem]
            updated: 2026-07-01
            ---

            # Raise Series A

            ## Stato
            term sheet in hand

            ## Movimento
            - 2026-06-01 — term sheet received — [[guilhem]] — source: rec://2026-06-01-call
            """);

        var g = new RepoMapGraph(_root);
        var obj = await g.GetObjectAsync(MapObjectKind.Goal, "series-a");

        Assert.NotNull(obj);
        Assert.Equal("Raise Series A", obj!.Frontmatter["name"]);
        Assert.Equal("active", obj.Frontmatter["status"]);
        Assert.Contains("## Stato", obj.BodyMarkdown);
        Assert.Contains("rec://2026-06-01-call", obj.Sources);
        Assert.Contains("map/goals/series-a.md", obj.Sources);
    }

    [Fact]
    public async Task Missing_object_returns_null_never_fabricated()
    {
        var g = new RepoMapGraph(_root);
        Assert.Null(await g.GetObjectAsync(MapObjectKind.Goal, "ghost"));
    }

    [Fact]
    public async Task Walk_goal_movimento_parses_dated_sourced_lines_newest_first()
    {
        Write("map/goals/g.md", """
            ---
            type: goal
            name: G
            status: active
            updated: 2026-07-01
            ---

            ## Movimento
            - 2026-05-01 — first move — [[g]] — source: rec://a
            - 2026-06-01 — second move — [[g]] — source: rec://b
            - a malformed line with no source — should be dropped
            """);

        var g = new RepoMapGraph(_root);
        var lines = await g.WalkTimelineAsync("goal:g", from: null, to: null);

        Assert.Equal(2, lines.Count);                 // the sourceless line is dropped (R1)
        Assert.Equal("2026-06-01", lines[0].Date);    // newest first
        Assert.Equal("rec://b", lines[0].Source);
        Assert.Contains("g", lines[0].Links);
    }

    [Fact]
    public async Task Walk_global_timeline_filters_by_linked_entity_and_date()
    {
        Write("map/timeline.md", """
            ---
            type: timeline
            ---

            - 2026-06-01 — met Guilhem — [[guilhem]] [[series-a]] — source: rec://x
            - 2026-06-15 — unrelated event — [[someone]] — source: rec://y
            - 2026-07-01 — later with Guilhem — [[guilhem]] — source: rec://z
            """);

        var g = new RepoMapGraph(_root);
        var all = await g.WalkTimelineAsync("person:guilhem", null, null);
        Assert.Equal(2, all.Count);

        var since = await g.WalkTimelineAsync("person:guilhem", from: "2026-06-20", to: null);
        Assert.Single(since);
        Assert.Equal("2026-07-01", since[0].Date);
    }

    [Fact]
    public async Task Neighbours_come_from_frontmatter_lists_and_wikilinks()
    {
        Write("map/people/guilhem.md", "---\ntype: person\nname: Guilhem\nupdated: 2026-07-01\n---\n\n## Chi è\ncofounder");
        Write("map/threads/series-a.md", "---\ntype: thread\nname: Series A\nstatus: active\nupdated: 2026-07-01\n---\n");
        Write("map/goals/g.md", """
            ---
            type: goal
            name: G
            status: active
            people: [guilhem]
            updated: 2026-07-01
            ---

            ## Stato
            linked to [[series-a]]
            """);

        var g = new RepoMapGraph(_root);
        var neighbours = await g.NeighboursAsync(MapObjectKind.Goal, "g");
        Assert.Contains(neighbours, n => n.Kind == MapObjectKind.Person && n.Slug == "guilhem");
        Assert.Contains(neighbours, n => n.Kind == MapObjectKind.Thread && n.Slug == "series-a");
    }

    [Fact]
    public async Task List_goal_slugs_skips_templates()
    {
        Write("map/goals/real.md", "---\ntype: goal\nname: Real\nstatus: active\nupdated: 2026-07-01\n---\n");
        Write("map/goals/_TEMPLATE.md", "---\ntype: goal\n---\n");
        var g = new RepoMapGraph(_root);
        var slugs = await g.ListGoalSlugsAsync();
        Assert.Contains("real", slugs);
        Assert.DoesNotContain("_TEMPLATE", slugs);
    }
}
