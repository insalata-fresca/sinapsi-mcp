namespace Cervello.Enrichment.Domain;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The cervello CONTEXT PACK — the typed, bounded, ranked, SOURCED working set the CT146 assembler
// returns (design §2.1, normative for the build spec §5.1). Every field here mirrors the design
// doc's JSON shape EXACTLY so the wire contract the bridge tools call is the contract this domain
// enforces. A pack is never a blob: every item carries a resolvable `source:` ref, `coverage` is
// mandatory (the antidote to confabulation), and bounding/summarising never strips provenance.
//
// These are the ENGINE-side domain records; ContextPackApi renders them to the exact snake_case
// wire JSON the design doc specifies (intent, focus, budget, used, as_of, sections[], coverage{},
// open_points[], delta{}, disambiguation[]).
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The dialogue intent a pack is shaped for (design §2.1). Each maps to a per-intent shape.</summary>
public enum PackIntent
{
    /// <summary>How a single goal is moving — goal object + its evidence timeline + ranked recent evidence + 1-hop neighbours.</summary>
    GoalReasoning,

    /// <summary>Shallow + wide sweep across every active goal (one evidence line each) + a cross-goal recent-activity ribbon.</summary>
    Portfolio,

    /// <summary>Prep for a person — dossier + linked threads + last-N interactions + evidence-linked goals.</summary>
    PersonPrep,

    /// <summary>Free-text recall — hybrid-search top-N across the corpus + the resolved map entity (if any).</summary>
    Recall,

    /// <summary>A thread — full dossier body + its people's one-liners + timeline tail + linked goals.</summary>
    Thread,
}

/// <summary>
/// One item in a pack section: a content string plus its resolvable <c>source:</c> ref and an
/// optional confidence (design §2.1: <c>{ ...content..., "source": "&lt;ref&gt;", "confidence": &lt;0..1?&gt; }</c>).
/// The invariant — <b>no source → the item is not in the pack</b> — is enforced by the ctor: an
/// item without a non-empty, registered-scheme ref cannot be constructed.
/// </summary>
public sealed record PackItem
{
    public PackItem(string content, string source, double? confidence = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("PackItem.Content must be non-empty", nameof(content));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("PackItem.Source must be a resolvable ref (design §2.1: no source → not in the pack)", nameof(source));
        if (!SourceRef.IsResolvableScheme(source))
            throw new ArgumentException($"PackItem.Source '{source}' is not a registered SCHEMAS §1 ref (pin://|rec://|drive://|gmail://|bundle://|<path>)", nameof(source));
        Content = content;
        Source = source;
        Confidence = confidence;
    }

    /// <summary>The item's content (a dossier line, a timeline line, a server-authored summary).</summary>
    public string Content { get; }

    /// <summary>The resolvable source ref backing the item (SCHEMAS §1). Inherited by any summary of it.</summary>
    public string Source { get; }

    /// <summary>Optional confidence (0..1) that travels to the caller — never flattened to fact (design §2.3.4).</summary>
    public double? Confidence { get; }

    /// <summary>The item's char cost against the budget (content only — refs/confidence are metadata).</summary>
    public int Cost => Content.Length;
}

/// <summary>A named, ordered group of ranked items (design §2.1 <c>sections[]</c>). Bounding STOPs at a section boundary.</summary>
public sealed record PackSection(string Section, IReadOnlyList<PackItem> Items)
{
    public int Cost => Items.Sum(i => i.Cost);
}

/// <summary>
/// The mandatory <c>coverage</c> block (design §2.1/§2.3) — how Claude knows the edges of what it
/// was given. <c>looked_at</c> = sources consulted; <c>deferred</c> = sources that exist but were
/// not pulled (phase-gated / out-of-budget); <c>gaps</c> = things Claude must NOT claim beyond.
/// </summary>
public sealed record PackCoverage(
    IReadOnlyList<string> LookedAt,
    IReadOnlyList<string> Deferred,
    IReadOnlyList<string> Gaps)
{
    public static PackCoverage Empty => new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>A pending open-point folded into the pack (design §2.1 <c>open_points[]</c> piggyback).</summary>
public sealed record PackOpenPoint(string PointId, string Question, string Kind);

/// <summary>One new evidence line surfaced by the delta diff (design §2.6).</summary>
public sealed record DeltaEvidence(string Date, string Fact, string Source);

/// <summary>A goal status transition surfaced by the delta diff (design §2.6).</summary>
public sealed record DeltaStatusChange(string Focus, string From, string To);

/// <summary>
/// The movement-since-last-look block (design §2.6) — populated for <c>goal_reasoning</c> +
/// <c>portfolio</c>, diffed against the caller's server-side baseline cursor.
/// </summary>
public sealed record PackDelta(
    string Since,
    IReadOnlyList<DeltaEvidence> NewEvidence,
    IReadOnlyList<DeltaStatusChange> StatusChanges);

/// <summary>A disambiguation candidate (design §2.1) — present only when a recall <c>focus</c> is ambiguous.</summary>
public sealed record DisambiguationCandidate(string Candidate, string Descriptor, int EvidenceCount);

/// <summary>
/// The full assembled pack (design §2.1). Immutable; rendered to the wire by <c>ContextPackApi</c>.
/// <c>Used</c> is the char count consumed against <c>Budget</c>; <c>AsOf</c> is the freshness stamp
/// (the index/repo HEAD the pack was built from). <c>Delta</c> / <c>Disambiguation</c> are null when
/// not applicable to the intent (they are then omitted from the wire JSON).
/// </summary>
public sealed record ContextPack
{
    public required PackIntent Intent { get; init; }
    public required string? Focus { get; init; }
    public required int Budget { get; init; }
    public required int Used { get; init; }
    public required string AsOf { get; init; }
    public required IReadOnlyList<PackSection> Sections { get; init; }
    public required PackCoverage Coverage { get; init; }
    public required IReadOnlyList<PackOpenPoint> OpenPoints { get; init; }
    public PackDelta? Delta { get; init; }
    public IReadOnlyList<DisambiguationCandidate>? Disambiguation { get; init; }
}
