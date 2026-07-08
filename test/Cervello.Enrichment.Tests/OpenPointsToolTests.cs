using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The two open-points MCP tools (spec <c>open-points-mcp</c>; tasks E5 4.1/4.2/4.3) — the operator's
/// only enrichment UI. Proves: list is redacted + filterable; answering a speaker point applies with
/// a <c>human://</c> basis AND enrolls; answering a correction point updates the glossary; dismiss
/// omits (never guessed); a resolved point can't be double-applied; the tools are token-gated (401
/// without a bearer) and every call is scoped + logged. All against fakes — no personal data, no
/// live store.
/// </summary>
public sealed class OpenPointsToolTests
{
    private const string Token = "test-open-points-token";
    private const string Bundle = "2026-07-01-standup";
    private const string Rec = "rec-2026-07-01-standup";

    private sealed record Harness(
        OpenPointsService Service,
        InMemoryOpenPointStore Store,
        FakeAccessLog Log,
        FakeMapPrWriter Pr,
        InMemoryCorrectionMapStore Glossary,
        InMemoryVoiceprintStore Voiceprints,
        FakeEnrollmentSourceProvider EnrollSource);

    private static Harness Build(params string[] enrollAllowlist)
    {
        var store = new InMemoryOpenPointStore();
        var log = new FakeAccessLog();
        var pr = new FakeMapPrWriter();
        var writer = new CervelloGraphWriter(pr, new FakeLinkResolver("guilhem", "marco"), new FakePinStore());
        var glossary = new InMemoryCorrectionMapStore();
        var allowlist = new EnrollmentAllowlist(enrollAllowlist);
        var voiceprints = new InMemoryVoiceprintStore(allowlist);
        var enrollment = new VoiceprintEnrollment(voiceprints);
        var enrollSource = new FakeEnrollmentSourceProvider();
        var gate = new TokenOpenPointsAuthGate(Token);
        var service = new OpenPointsService(gate, store, log, writer, glossary, enrollment, allowlist, enrollSource);
        return new Harness(service, store, log, pr, glossary, voiceprints, enrollSource);
    }

    private static OpenPoint SpeakerPoint(string id, params (string value, double conf, string why)[] cands) =>
        new(id, OpenPointKind.Speaker, Rec, Bundle, "which enrolled person is s1?",
            cands.Select(c => new ScoredCandidate(c.value, c.conf, c.why)).ToList(), mergedSpeaker: "s1");

    private static OpenPoint CorrectionPoint(string id, params string[] cands) =>
        new(id, OpenPointKind.Correction, Rec, Bundle, "which correction is right?",
            cands.Select(c => new ScoredCandidate(c, 0.5, "candidate")).ToList());

    // ── 4.1 list: redacted pending points ───────────────────────────────────────────────────────
    [Fact]
    public async Task List_returns_redacted_pending_points_with_scored_candidates()
    {
        var h = Build();
        await h.Store.EnqueueAsync(SpeakerPoint("op_1", ("guilhem", 0.55, "voice 0.55; filename prior")));

        var views = await h.Service.ListAsync(Token);

        var v = Assert.Single(views);
        Assert.Equal("op_1", v.PointId);
        Assert.Equal("speaker", v.KindWire);
        Assert.Equal($"rec://{Rec}", v.Recording);
        Assert.Equal($"bundle://{Bundle}", v.Bundle);
        var c = Assert.Single(v.Candidates);
        Assert.Equal("guilhem", c.Value);
        Assert.Equal(0.55, c.Confidence, 3);
        Assert.Equal("voice 0.55; filename prior", c.Why);
        // Redaction: the serialized view carries no body/audio/vector — only refs + question + candidates.
        var json = System.Text.Json.JsonSerializer.Serialize(v);
        Assert.DoesNotContain("body", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audio", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── 4.1 list: filter by kind ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task List_filters_by_kind()
    {
        var h = Build();
        await h.Store.EnqueueAsync(SpeakerPoint("op_s", ("guilhem", 0.5, "voice")));
        await h.Store.EnqueueAsync(CorrectionPoint("op_c", "TotalEnergies", "Total Energies"));

        var speakers = await h.Service.ListAsync(Token, kind: OpenPointKind.Speaker);

        var v = Assert.Single(speakers);
        Assert.Equal("op_s", v.PointId);
    }

    [Fact]
    public async Task List_filters_by_recording()
    {
        var h = Build();
        await h.Store.EnqueueAsync(SpeakerPoint("op_a", ("guilhem", 0.5, "voice")));
        await h.Store.EnqueueAsync(new OpenPoint("op_b", OpenPointKind.Speaker, "rec-other", "b-2026", "q",
            new[] { new ScoredCandidate("marco", 0.5, "voice") }, mergedSpeaker: "s1"));

        var onlyThis = await h.Service.ListAsync(Token, recording: Rec);

        Assert.Equal("op_a", Assert.Single(onlyThis).PointId);
    }

    // ── 4.2 answer speaker → applies with human basis AND enrolls ───────────────────────────────
    [Fact]
    public async Task Answering_a_speaker_point_applies_with_human_basis_and_enrolls()
    {
        var h = Build(enrollAllowlist: "guilhem");
        await h.Store.EnqueueAsync(SpeakerPoint("op_1", ("guilhem", 0.55, "voice 0.55; filename prior")));
        h.EnrollSource.Seed(Rec, "s1", new EnrollmentSource(TestVectors.Axis(3), new[] { $"rec://{Rec}#s1" }, 0.55));

        var result = await h.Service.AnswerAsync(Token, "op_1", OpenPointAnswer.Select("guilhem"), new DateOnly(2026, 7, 1));

        Assert.Equal(AnswerStatus.Applied, result.Status);
        Assert.Equal("human://op_1", result.BasisId);           // R9: human basis
        Assert.NotNull(result.Pr);
        // the map mutation carries the human basis + a rec:// source
        var m = Assert.Single(h.Pr.LastPr!.Mutations);
        Assert.Equal("human://op_1", m.BasisId);
        Assert.Contains("source: rec://", m.Content);
        // point resolved + enrolled
        Assert.True(await h.Store.IsResolvedAsync("op_1"));
        Assert.True(result.Enrolled);
        var print = await h.Voiceprints.GetAsync("guilhem");
        Assert.NotNull(print);
    }

    // ── 4.2 answer correction → updates glossary (auto-corrects next time) ──────────────────────
    [Fact]
    public async Task Answering_a_correction_point_updates_the_glossary()
    {
        var h = Build();
        await h.Store.EnqueueAsync(CorrectionPoint("op_c", "Total Energies", "TotalEnergies"));

        var result = await h.Service.AnswerAsync(Token, "op_c", OpenPointAnswer.Select("TotalEnergies"), new DateOnly(2026, 7, 1));

        Assert.Equal(AnswerStatus.Applied, result.Status);
        Assert.True(result.GlossaryUpdated);
        var glossary = await h.Glossary.GetGlossaryAsync();
        var entry = Assert.Single(glossary);
        Assert.Equal("TotalEnergies", entry.After);            // corrects TO the operator's choice
        Assert.Equal("human://op_c", entry.ConfirmedAnswerId); // learning signal carries the basis
    }

    // ── 4.2 dismiss → omit, never guessed; recorded ─────────────────────────────────────────────
    [Fact]
    public async Task Dismiss_omits_without_guessing_and_records_the_dismissal()
    {
        var h = Build();
        await h.Store.EnqueueAsync(SpeakerPoint("op_1", ("guilhem", 0.5, "voice")));

        var result = await h.Service.AnswerAsync(Token, "op_1", OpenPointAnswer.Dismiss(), new DateOnly(2026, 7, 1));

        Assert.Equal(AnswerStatus.Dismissed, result.Status);
        Assert.Null(result.Pr);                          // NO fact written (speaker stays unidentified)
        Assert.Equal(0, h.Pr.Opened);
        Assert.True(await h.Store.IsResolvedAsync("op_1"));
        Assert.Contains(h.Log.Entries, e => e.Outcome == "dismissed" && e.PointId == "op_1"); // recorded
    }

    // ── 4.2 idempotent: a resolved point can't be double-applied ────────────────────────────────
    [Fact]
    public async Task A_resolved_point_cannot_be_double_applied()
    {
        var h = Build(enrollAllowlist: "guilhem");
        await h.Store.EnqueueAsync(SpeakerPoint("op_1", ("guilhem", 0.55, "voice")));
        h.EnrollSource.Seed(Rec, "s1", new EnrollmentSource(TestVectors.Axis(3), new[] { $"rec://{Rec}#s1" }, 0.55));

        var first = await h.Service.AnswerAsync(Token, "op_1", OpenPointAnswer.Select("guilhem"), new DateOnly(2026, 7, 1));
        var second = await h.Service.AnswerAsync(Token, "op_1", OpenPointAnswer.Select("guilhem"), new DateOnly(2026, 7, 1));

        Assert.Equal(AnswerStatus.Applied, first.Status);
        Assert.Equal(AnswerStatus.AlreadyResolved, second.Status);
        Assert.Equal(1, h.Pr.Opened);                    // exactly ONE map PR — no double write
    }

    // ── 4.3 token gate: 401 (no bearer / wrong bearer), before any I/O ──────────────────────────
    [Fact]
    public async Task List_without_a_token_is_401()
    {
        var h = Build();
        await h.Store.EnqueueAsync(SpeakerPoint("op_1", ("guilhem", 0.5, "voice")));

        await Assert.ThrowsAsync<OpenPointsUnauthorizedException>(() => h.Service.ListAsync(presentedToken: null));
        await Assert.ThrowsAsync<OpenPointsUnauthorizedException>(() => h.Service.ListAsync(presentedToken: "wrong-token"));
    }

    [Fact]
    public async Task Answer_without_a_token_is_401_and_writes_nothing()
    {
        var h = Build();
        await h.Store.EnqueueAsync(SpeakerPoint("op_1", ("guilhem", 0.5, "voice")));

        await Assert.ThrowsAsync<OpenPointsUnauthorizedException>(() =>
            h.Service.AnswerAsync(null, "op_1", OpenPointAnswer.Select("guilhem"), new DateOnly(2026, 7, 1)));
        Assert.Equal(0, h.Pr.Opened);
        Assert.False(await h.Store.IsResolvedAsync("op_1")); // untouched
    }

    [Fact]
    public void An_unconfigured_gate_fails_closed()
    {
        // SearchAuth lesson: an unset token must NOT open the surface.
        var gate = new TokenOpenPointsAuthGate(expectedToken: null);
        Assert.Throws<OpenPointsUnauthorizedException>(() => gate.Authorize("anything"));
    }

    // ── 4.3 scoped + logged ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Every_call_is_scoped_to_cervello_and_logged()
    {
        var h = Build();
        await h.Store.EnqueueAsync(SpeakerPoint("op_1", ("guilhem", 0.5, "voice")));

        await h.Service.ListAsync(Token);
        await h.Service.AnswerAsync(Token, "op_1", OpenPointAnswer.Dismiss(), new DateOnly(2026, 7, 1));

        Assert.Equal(2, h.Log.Entries.Count);
        Assert.All(h.Log.Entries, e => Assert.Equal(OpenPointsCaller.CervelloScope, e.Scope));
        Assert.Contains(h.Log.Entries, e => e.Tool == "cervello_open_points_list");
        Assert.Contains(h.Log.Entries, e => e.Tool == "cervello_open_points_answer");
    }

    // ── a non-allowlisted speaker is still attributed, but NO biometric enroll happens ──────────
    [Fact]
    public async Task Answering_a_speaker_not_on_the_allowlist_attributes_but_does_not_enroll()
    {
        var h = Build(/* empty allowlist */);
        await h.Store.EnqueueAsync(SpeakerPoint("op_1", ("marco", 0.55, "voice")));
        h.EnrollSource.Seed(Rec, "s1", new EnrollmentSource(TestVectors.Axis(3), new[] { $"rec://{Rec}#s1" }, 0.55));

        var result = await h.Service.AnswerAsync(Token, "op_1", OpenPointAnswer.Select("marco"), new DateOnly(2026, 7, 1));

        Assert.Equal(AnswerStatus.Applied, result.Status); // human-confirmed attribution still written
        Assert.NotNull(result.Pr);
        Assert.False(result.Enrolled);                     // §10 gate: no centroid written
        Assert.Null(await h.Voiceprints.GetAsync("marco"));
    }
}
