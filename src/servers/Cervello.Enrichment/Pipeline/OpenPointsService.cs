using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// The engine behind the two open-points MCP tools (spec <c>open-points-mcp</c>) — the operator's
/// ONLY enrichment UI (Claude web/mobile via the cervello connector, Surface A). It is:
///
/// <list type="bullet">
/// <item><b>token-gated</b> — every call authorizes through <see cref="IOpenPointsAuthGate"/> FIRST
///   (401 on a missing/invalid bearer, like M5's <c>/search</c>; SearchAuth lesson);</item>
/// <item><b>scoped + logged</b> — operates only in cervello scope and appends every call to the
///   access log;</item>
/// <item><b>redacted</b> — <see cref="ListAsync"/> projects each point to an <see cref="OpenPointView"/>
///   carrying refs + a one-line question + scored candidates and NOTHING else (R10);</item>
/// <item><b>the learning signal</b> — <see cref="AnswerAsync"/> applies the confirmed fact with a
///   <c>human://&lt;answer-id&gt;</c> basis, resolves the point (idempotent — a resolved point can't be
///   double-applied), updates the glossary for a correction, and enrolls/refines the voiceprint for
///   a speaker; a dismiss omits the fact (never guessed) and records the dismissal.</item>
/// </list>
///
/// <para>The map-PR / glossary / voiceprint writes go through the same seams E4/E3 built
/// (<see cref="CervelloGraphWriter"/>, <see cref="ICorrectionMapStore"/>,
/// <see cref="VoiceprintEnrollment"/>) — so the live adapters swap in at deploy with no logic
/// change. All exercised against fakes.</para>
/// </summary>
public sealed class OpenPointsService(
    IOpenPointsAuthGate authGate,
    IOpenPointStore store,
    IAccessLog accessLog,
    CervelloGraphWriter graphWriter,
    ICorrectionMapStore correctionMap,
    VoiceprintEnrollment enrollment,
    EnrollmentAllowlist enrollmentAllowlist,
    IEnrollmentSourceProvider enrollmentSource,
    ILogger<OpenPointsService>? logger = null)
{
    private readonly IOpenPointsAuthGate _auth = authGate ?? throw new ArgumentNullException(nameof(authGate));
    private readonly IOpenPointStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAccessLog _log = accessLog ?? throw new ArgumentNullException(nameof(accessLog));
    private readonly CervelloGraphWriter _graph = graphWriter ?? throw new ArgumentNullException(nameof(graphWriter));
    private readonly ICorrectionMapStore _correctionMap = correctionMap ?? throw new ArgumentNullException(nameof(correctionMap));
    private readonly VoiceprintEnrollment _enroll = enrollment ?? throw new ArgumentNullException(nameof(enrollment));
    private readonly EnrollmentAllowlist _allowlist = enrollmentAllowlist ?? throw new ArgumentNullException(nameof(enrollmentAllowlist));
    private readonly IEnrollmentSourceProvider _enrollSource = enrollmentSource ?? throw new ArgumentNullException(nameof(enrollmentSource));
    private readonly ILogger _logger = logger ?? NullLogger<OpenPointsService>.Instance;

    /// <summary>
    /// <c>cervello_open_points_list(kind?, recording?)</c>: the operator's pending open-points, each
    /// REDACTED (refs + question + scored candidates only — R10), filterable by kind + recording.
    /// </summary>
    public async Task<IReadOnlyList<OpenPointView>> ListAsync(
        string? presentedToken, OpenPointKind? kind = null, string? recording = null, CancellationToken ct = default)
    {
        var caller = _auth.Authorize(presentedToken); // 401 if missing/invalid — BEFORE any store I/O
        var pending = await _store.ListPendingAsync(recording, ct).ConfigureAwait(false);
        var views = pending
            .Where(p => kind is null || p.Kind == kind)
            .Select(OpenPointView.From)
            .ToList();
        await _log.AppendAsync(new AccessLogEntry("cervello_open_points_list", caller.Scope,
            Outcome: $"listed:{views.Count}"), ct).ConfigureAwait(false);
        return views;
    }

    /// <summary>
    /// <c>cervello_open_points_answer(point_id, answer)</c>: apply the confirmed fact (map PR with
    /// <c>human://&lt;answer-id&gt;</c> basis), resolve the point, and fire the learning signal
    /// (glossary upsert for a correction; enroll/refine for a speaker). Idempotent — answering an
    /// already-resolved point is a no-op that reports <see cref="AnswerStatus.AlreadyResolved"/>.
    /// A dismiss omits the fact (never guessed) and records the dismissal.
    /// </summary>
    public async Task<AnswerResult> AnswerAsync(
        string? presentedToken, string pointId, OpenPointAnswer answer, DateOnly on, CancellationToken ct = default)
    {
        var caller = _auth.Authorize(presentedToken);
        ArgumentNullException.ThrowIfNull(answer);
        if (string.IsNullOrWhiteSpace(pointId))
            throw new ArgumentException("pointId must be non-empty", nameof(pointId));

        var point = await _store.GetAsync(pointId, ct).ConfigureAwait(false);
        if (point is null)
        {
            await _log.AppendAsync(new AccessLogEntry("cervello_open_points_answer", caller.Scope, "unknown_point", pointId), ct)
                .ConfigureAwait(false);
            return AnswerResult.Unknown(pointId);
        }
        if (await _store.IsResolvedAsync(pointId, ct).ConfigureAwait(false))
        {
            await _log.AppendAsync(new AccessLogEntry("cervello_open_points_answer", caller.Scope, "already_resolved", pointId), ct)
                .ConfigureAwait(false);
            return AnswerResult.AlreadyResolved(pointId);
        }

        // ── dismiss: omit the fact, never guessed; record the dismissal ─────────────────────────
        if (answer.Mode == AnswerMode.Dismiss)
        {
            var answerId = $"op_{pointId}"; // stable dismissal id for the log
            var claimed = await _store.ResolveAsync(pointId, OpenPointResolution.DismissedBy(answerId), ct).ConfigureAwait(false);
            if (!claimed) return AnswerResult.AlreadyResolved(pointId); // lost the race — someone resolved first
            await _log.AppendAsync(new AccessLogEntry("cervello_open_points_answer", caller.Scope, "dismissed", pointId), ct)
                .ConfigureAwait(false);
            _logger.LogInformation("open-point {Point} dismissed — fact omitted, never guessed", pointId);
            return AnswerResult.Dismissed(pointId);
        }

        // ── resolving answer: derive the confirmed value + a human:// basis ─────────────────────
        var confirmedValue = ResolveConfirmedValue(point, answer);
        var basis = ConfirmationBasis.Human(pointId);              // human://<point/answer id>

        // CLAIM the resolution FIRST (single-shot) so no write happens twice on a re-run.
        var resolution = OpenPointResolution.Answered(confirmedValue, basis.Id, pointId);
        var won = await _store.ResolveAsync(pointId, resolution, ct).ConfigureAwait(false);
        if (!won) return AnswerResult.AlreadyResolved(pointId); // already applied — double-apply guard

        MapPrHandle? pr = null;
        var enrolled = false;
        var glossaryUpdated = false;

        switch (point.Kind)
        {
            case OpenPointKind.Speaker:
                pr = await ApplySpeakerAsync(point, confirmedValue, basis, on, ct, e => enrolled = e).ConfigureAwait(false);
                break;
            case OpenPointKind.Correction:
                (pr, glossaryUpdated) = await ApplyCorrectionAsync(point, confirmedValue, basis, ct).ConfigureAwait(false);
                break;
            case OpenPointKind.Fact:
                pr = await ApplyFactAsync(point, confirmedValue, basis, ct).ConfigureAwait(false);
                break;
        }

        await _log.AppendAsync(new AccessLogEntry("cervello_open_points_answer", caller.Scope,
            Outcome: $"applied:{point.Kind}:{(pr is null ? "no-pr" : pr.Branch)}", pointId), ct).ConfigureAwait(false);
        return AnswerResult.Applied(pointId, point.Kind, basis.Id, pr, enrolled, glossaryUpdated);
    }

    // ── speaker: write the attribution to map/ with human basis + enroll/refine the voiceprint ──
    private async Task<MapPrHandle?> ApplySpeakerAsync(
        OpenPoint point, string person, ConfirmationBasis basis, DateOnly on, CancellationToken ct, Action<bool> setEnrolled)
    {
        var merged = point.MergedSpeaker ?? person;
        var sourceRef = $"rec://{Strip(point.RecordingId)}#{merged}";
        var mutation = new MapMutation(
            dossierPath: $"map/people/{person}.md",
            section: "## Timeline",
            content: $"- {on:yyyy-MM-dd} — spoke in rec://{Strip(point.RecordingId)} ({merged}) — source: {sourceRef}",
            source: sourceRef,
            confidence: 1.0,                 // an operator confirmation is certain
            bundleId: point.BundleId,
            basisId: basis.Id);              // human://<answer-id> — R9
        var pr = await _graph.OpenReviewPrAsync(
            new GraphAddRequest(point.BundleId, [mutation], [new ReferencedLink(person, "person")]), ct)
            .ConfigureAwait(false);

        // Learning signal: enroll/refine ONLY if the person is on the §10 allowlist AND a confirmed
        // centroid is available. A non-allowlisted person is still attributed (human confirmation),
        // but NO biometric write happens — the enroll is skipped, not forced.
        if (_allowlist.IsAllowed(person))
        {
            var src = await _enrollSource.GetConfirmedSourceAsync(Strip(point.RecordingId), merged, ct).ConfigureAwait(false);
            if (src is not null)
            {
                await _enroll.EnrollOnConfirmationAsync(person, src.Centroid, src.SourceSegments, src.MatchCosine, basis, on, ct)
                    .ConfigureAwait(false);
                setEnrolled(true);
            }
        }
        return pr;
    }

    // ── correction: update the glossary so the term auto-corrects next time ─────────────────────
    private async Task<(MapPrHandle? pr, bool glossaryUpdated)> ApplyCorrectionAsync(
        OpenPoint point, string correctedTo, ConfirmationBasis basis, CancellationToken ct)
    {
        // The "before" term is the point's subject; the operator's answer is the "after".
        var before = FirstCandidateOtherThan(point, correctedTo) ?? point.QuestionRedacted;
        var entry = new GlossaryEntry(before, correctedTo, CorrectionKind.Term, confirmedAnswerId: basis.Id);
        await _correctionMap.UpsertAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation("open-point {Point} correction answered → glossary '{Before}'→'{After}' (auto-corrects next time)",
            point.PointId, before, correctedTo);
        // A correction is transcript-scoped (not a map mutation) — like the apply stage, it does not
        // open a map PR; the glossary upsert IS the applied fact.
        return (null, true);
    }

    // ── link/timeline fact: write it to map/ with the human basis ───────────────────────────────
    private async Task<MapPrHandle?> ApplyFactAsync(OpenPoint point, string value, ConfirmationBasis basis, CancellationToken ct)
    {
        var sourceRef = $"rec://{Strip(point.RecordingId)}";
        var mutation = new MapMutation(
            dossierPath: "map/timeline.md",
            section: "## Timeline",
            content: $"- {value} — source: {sourceRef}",
            source: sourceRef,
            confidence: 1.0,
            bundleId: point.BundleId,
            basisId: basis.Id);
        return await _graph.OpenReviewPrAsync(
            new GraphAddRequest(point.BundleId, [mutation], Array.Empty<ReferencedLink>()), ct).ConfigureAwait(false);
    }

    private static string ResolveConfirmedValue(OpenPoint point, OpenPointAnswer answer)
    {
        if (answer.Mode == AnswerMode.Value) return answer.Value!;
        // Select: the value must be one of the point's candidates.
        var v = answer.Value!;
        if (point.Candidates.All(c => !string.Equals(c.Value, v, StringComparison.Ordinal)))
            throw new ArgumentException($"select answer '{v}' is not a candidate of point '{point.PointId}'");
        return v;
    }

    private static string? FirstCandidateOtherThan(OpenPoint point, string value) =>
        point.Candidates.FirstOrDefault(c => !string.Equals(c.Value, value, StringComparison.Ordinal))?.Value;

    private static string Strip(string recordingId) =>
        recordingId.StartsWith("rec://", StringComparison.Ordinal) ? recordingId["rec://".Length..] : recordingId;
}

/// <summary>The status of an answer call.</summary>
public enum AnswerStatus { Applied, Dismissed, AlreadyResolved, UnknownPoint }

/// <summary>The result of answering an open-point.</summary>
public sealed record AnswerResult
{
    private AnswerResult(AnswerStatus status, string pointId, OpenPointKind? kind, string? basisId,
        MapPrHandle? pr, bool enrolled, bool glossaryUpdated)
    {
        Status = status; PointId = pointId; Kind = kind; BasisId = basisId; Pr = pr;
        Enrolled = enrolled; GlossaryUpdated = glossaryUpdated;
    }

    public AnswerStatus Status { get; }
    public string PointId { get; }
    public OpenPointKind? Kind { get; }

    /// <summary>The <c>human://&lt;answer-id&gt;</c> basis written for an applied fact.</summary>
    public string? BasisId { get; }

    /// <summary>The opened map review-PR, if the answer wrote a map fact (null for a correction/dismiss).</summary>
    public MapPrHandle? Pr { get; }

    /// <summary>Whether the answer enrolled/refined a voiceprint (speaker + allowlisted + source present).</summary>
    public bool Enrolled { get; }

    /// <summary>Whether the answer upserted the glossary (correction).</summary>
    public bool GlossaryUpdated { get; }

    public bool Resolved => Status is AnswerStatus.Applied or AnswerStatus.Dismissed;

    public static AnswerResult Applied(string pointId, OpenPointKind kind, string basisId, MapPrHandle? pr, bool enrolled, bool glossaryUpdated) =>
        new(AnswerStatus.Applied, pointId, kind, basisId, pr, enrolled, glossaryUpdated);
    public static AnswerResult Dismissed(string pointId) =>
        new(AnswerStatus.Dismissed, pointId, null, null, null, false, false);
    public static AnswerResult AlreadyResolved(string pointId) =>
        new(AnswerStatus.AlreadyResolved, pointId, null, null, null, false, false);
    public static AnswerResult Unknown(string pointId) =>
        new(AnswerStatus.UnknownPoint, pointId, null, null, null, false, false);
}
