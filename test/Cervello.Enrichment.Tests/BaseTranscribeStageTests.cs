using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// text-correction — "Base transcript is the correction substrate" / "Base transcript persisted
/// before correction", RECONCILED to the ratified design (MISSION E-BASE): <b>the Google <c>.txt</c>
/// transcript IS the base</b> (verbatim), persisted at recordings/transcripts/&lt;id&gt;.md and
/// written once. CT126 re-transcription is an OPTIONAL fallback (default OFF) used ONLY when a
/// recording carries no Google <c>.txt</c> — it is never a hard dependency of a drain.
/// </summary>
public sealed class BaseTranscribeStageTests
{
    private static RecordingRef Rec() => new("20260601-guilhem", "sha-aaa", "m4a", "fr", ready: true);
    private static ReadOnlyMemory<byte> Audio() => new byte[] { 1, 2, 3, 4 };

    // ---- RATIFIED: the Google .txt IS the base (verbatim); CT126 is NOT called ----
    [Fact]
    public async Task Google_txt_is_the_base_persisted_verbatim_and_ct126_is_not_called()
    {
        var google = new FakeBaseTranscriptSource(new BaseTranscript("# Guilhem 1-1\n\nbonjour…", "fr"));
        var ct126 = new FakeTranscribeClient(new BaseTranscript("SHOULD-NOT-BE-USED", "fr"));
        var store = new InMemoryTranscriptStore();
        // Even with a CT126 client wired AND enabled, a present Google base wins and CT126 is untouched.
        var stage = new BaseTranscribeStage(google, store, ct126, reTranscribeEnabled: true);

        var result = await stage.TranscribeAsync(Rec(), Audio());

        Assert.Equal("recordings/transcripts/20260601-guilhem.md", result.TranscriptPath);
        Assert.Equal(BaseSource.GoogleTxt, result.Source);
        Assert.False(result.AlreadyExisted);
        Assert.True(result.HasBase);
        Assert.Equal(0, ct126.Calls); // CT126 NOT called — the Google .txt is the base
        var persisted = store.Read("20260601-guilhem");
        Assert.NotNull(persisted);
        Assert.Equal("# Guilhem 1-1\n\nbonjour…", persisted!.Markdown); // verbatim
        Assert.Equal("fr", persisted.Language);
    }

    // ---- No Google .txt + fallback DISABLED (default) → NO base produced (never fabricated), no CT126 ----
    [Fact]
    public async Task No_google_txt_and_fallback_disabled_yields_no_base_without_calling_ct126()
    {
        var ct126 = new FakeTranscribeClient(new BaseTranscript("SHOULD-NOT-BE-USED", "fr"));
        var store = new InMemoryTranscriptStore();
        // Default posture: no re-transcribe fallback wired at all.
        var stage = new BaseTranscribeStage(FakeBaseTranscriptSource.None(), store);

        var result = await stage.TranscribeAsync(Rec(), Audio());

        Assert.Equal(BaseSource.NoBase, result.Source);
        Assert.False(result.HasBase);
        Assert.Null(store.Read("20260601-guilhem")); // nothing persisted — never fabricated
        Assert.Equal(0, ct126.Calls);
    }

    // ---- No Google .txt + fallback ENABLED → CT126 re-transcription fallback produces the base ----
    [Fact]
    public async Task No_google_txt_but_fallback_enabled_uses_ct126_retranscription()
    {
        var ct126 = new FakeTranscribeClient(new BaseTranscript("ct126 fallback base", "fr"));
        var store = new InMemoryTranscriptStore();
        var stage = new BaseTranscribeStage(FakeBaseTranscriptSource.None(), store, ct126, reTranscribeEnabled: true);

        var result = await stage.TranscribeAsync(Rec(), Audio());

        Assert.Equal(BaseSource.Ct126Fallback, result.Source);
        Assert.True(result.HasBase);
        Assert.Equal(1, ct126.Calls);
        Assert.Equal("ct126 fallback base", store.Read("20260601-guilhem")!.Markdown);
        Assert.Equal(("m4a", "fr", 4), ct126.Seen.Single());
    }

    // ---- The base is written once — a second run is a no-op and never overwrites ----
    [Fact]
    public async Task Base_transcript_is_not_re_resolved_or_overwritten_on_a_second_run()
    {
        var google = new FakeBaseTranscriptSource(new BaseTranscript("first", "fr"));
        var store = new InMemoryTranscriptStore();
        var stage = new BaseTranscribeStage(google, store);

        await stage.TranscribeAsync(Rec(), Audio());
        var second = await stage.TranscribeAsync(Rec(), Audio());

        Assert.True(second.AlreadyExisted);
        Assert.Equal(BaseSource.AlreadyExists, second.Source);
        Assert.Equal(1, google.Calls); // NOT re-resolved
        Assert.Equal("first", store.Read("20260601-guilhem")!.Markdown); // unchanged
    }

    // ---- The in-memory store models the immutability guard: a direct overwrite throws ----
    [Fact]
    public async Task Store_refuses_to_overwrite_an_existing_base()
    {
        var store = new InMemoryTranscriptStore();
        await store.WriteBaseAsync("id", new BaseTranscript("a", "fr"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.WriteBaseAsync("id", new BaseTranscript("b", "fr")));
    }
}
