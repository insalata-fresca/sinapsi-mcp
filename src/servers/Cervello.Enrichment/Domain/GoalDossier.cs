using System.Text;

namespace Cervello.Enrichment.Domain;

/// <summary>
/// The net-new <c>type: goal</c> map object (design §3.1, MC Q1 ratified). Renders the SCHEMAS-style
/// dossier at <c>map/goals/&lt;slug&gt;.md</c> — frontmatter (mirrors thread + goal-specific fields)
/// + the required body sections IN ORDER (<c>## Obiettivo</c>, <c>## Stato</c>, <c>## Movimento</c>,
/// <c>## Prossimi passi</c>). <c>## Movimento</c> is the evidence timeline in the exact SCHEMAS §4
/// grammar (MC Q3), so LINT R1/R2/R3/R11 apply unchanged — no new lint rule.
///
/// <para>MC Q2: status vocabulary is <c>active | achieved | stalled | dropped</c> (the ratified
/// four-value set). A status outside this set is rejected at construction (fail-closed).</para>
/// </summary>
public sealed record GoalDossier
{
    /// <summary>The MC-ratified status vocabulary (Q2): active | achieved | stalled | dropped.</summary>
    public static readonly string[] Statuses = ["active", "achieved", "stalled", "dropped"];

    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string? Horizon { get; init; }
    public IReadOnlyList<string> People { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Threads { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? Objective { get; init; }
    public string ObjectiveSource { get; init; } = "";
    public IReadOnlyList<TimelineLine> Movimento { get; init; } = Array.Empty<TimelineLine>();
    public IReadOnlyList<string> NextSteps { get; init; } = Array.Empty<string>();
    public required string Updated { get; init; }

    /// <summary>The repo-relative dossier path (design §3.1).</summary>
    public string Path => $"map/goals/{Slug}.md";

    /// <summary>Validate the status against the MC-ratified vocabulary (Q2); throw naming the offender.</summary>
    public static void ValidateStatus(string status)
    {
        if (!Array.Exists(Statuses, s => s.Equals(status, StringComparison.Ordinal)))
            throw new ArgumentException(
                $"goal status '{status}' is invalid: expected one of {string.Join('|', Statuses)}", nameof(status));
    }

    /// <summary>
    /// Render the full dossier markdown (frontmatter + the four body sections in order). Deterministic
    /// so a preview (<c>confirm=false</c>) shows byte-for-byte what the review-PR will write.
    /// </summary>
    public string Render()
    {
        ValidateStatus(Status);
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("type: goal\n");
        sb.Append("name: ").Append(Name).Append('\n');
        sb.Append("status: ").Append(Status).Append('\n');
        if (!string.IsNullOrWhiteSpace(Horizon)) sb.Append("horizon: ").Append(Horizon).Append('\n');
        if (People.Count > 0) sb.Append("people: [").Append(string.Join(", ", People)).Append("]\n");
        if (Threads.Count > 0) sb.Append("threads: [").Append(string.Join(", ", Threads)).Append("]\n");
        if (Tags.Count > 0) sb.Append("tags: [").Append(string.Join(", ", Tags)).Append("]\n");
        sb.Append("updated: ").Append(Updated).Append('\n');
        sb.Append("---\n\n");

        sb.Append("# ").Append(Name).Append("\n\n");

        sb.Append("## Obiettivo\n");
        if (!string.IsNullOrWhiteSpace(Objective))
        {
            sb.Append(Objective!.TrimEnd());
            if (!string.IsNullOrWhiteSpace(ObjectiveSource))
                sb.Append("  *(source: ").Append(ObjectiveSource).Append(")*");
            sb.Append('\n');
        }
        sb.Append('\n');

        sb.Append("## Stato\n\n");

        sb.Append("## Movimento\n");
        foreach (var m in Movimento)
            sb.Append(RenderMovimentoLine(m)).Append('\n');
        sb.Append('\n');

        sb.Append("## Prossimi passi\n");
        foreach (var n in NextSteps)
            sb.Append("- ").Append(n).Append('\n');

        return sb.ToString();
    }

    /// <summary>Render a single <c>## Movimento</c> line in the SCHEMAS §4 grammar.</summary>
    public static string RenderMovimentoLine(TimelineLine m)
    {
        var links = m.Links.Count > 0 ? " " + string.Join(" ", m.Links.Select(l => $"[[{l}]]")) : "";
        return $"- {m.Date} — {m.Fact} —{links} source: {m.Source}";
    }
}
