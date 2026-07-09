using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Deterministic fake correction LLM (spec <c>text-correction</c>). Returns a SCRIPTED set of
/// proposed candidates — the same proposal the real brain-api pass would return — so the stage's
/// evidence-gating is exercised without a live endpoint. The candidates are UNGATED on purpose:
/// the test proves the stage drops the unbacked ones.
/// </summary>
internal sealed class FakeCorrectionLlm(IReadOnlyList<CorrectionCandidate> proposals) : ICorrectionLlm
{
    public int Calls { get; private set; }
    public string? LastBaseText { get; private set; }

    public Task<IReadOnlyList<CorrectionCandidate>> ProposeAsync(
        string baseText, CorrectionContext context, CancellationToken ct = default)
    {
        Calls++;
        LastBaseText = baseText;
        return Task.FromResult(proposals);
    }
}

/// <summary>
/// A correction LLM that always THROWS — simulates the Brain-API (CT139) <c>/v1/enrich/correct</c>
/// Claude call 502-ing / timing out. Proves the correction pass is GRACEFUL at the orchestrator: a
/// failed correction is logged and skipped (base transcript left as-is), the drain still reaches
/// graph_pr_opened rather than failing the recording.
/// </summary>
internal sealed class ThrowingCorrectionLlm : ICorrectionLlm
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<CorrectionCandidate>> ProposeAsync(
        string baseText, CorrectionContext context, CancellationToken ct = default)
    {
        Calls++;
        throw new Adapters.CorrectionLlmException("correction LLM 502: Bad Gateway", retryable: true);
    }
}

/// <summary>
/// Deterministic fake selective-re-ASR client. Returns a scripted clarification per span, and
/// COUNTS calls so a test can prove re-ASR runs on exactly the garbled spans, never the whole
/// transcript. No live endpoint, no audio.
/// </summary>
internal sealed class FakeReAsrClient(IReadOnlyDictionary<(int, int), ReAsrResult>? scripted = null) : IReAsrClient
{
    private readonly IReadOnlyDictionary<(int, int), ReAsrResult> _scripted =
        scripted ?? new Dictionary<(int, int), ReAsrResult>();

    public int Calls { get; private set; }
    public List<TextSpan> Seen { get; } = [];

    public Task<ReAsrResult> ReAsrAsync(string recordingId, TextSpan span, CancellationToken ct = default)
    {
        Calls++;
        Seen.Add(span);
        return Task.FromResult(_scripted.GetValueOrDefault((span.Start, span.End), ReAsrResult.Unclear));
    }
}

/// <summary>
/// A re-ASR client that always THROWS (CT126 unreachable / transient) — proves the correction stage
/// GRACEFULLY skips the span (omits it, leaves as-is) instead of failing the drain.
/// </summary>
internal sealed class ThrowingReAsrClient : IReAsrClient
{
    public int Calls { get; private set; }

    public Task<ReAsrResult> ReAsrAsync(string recordingId, TextSpan span, CancellationToken ct = default)
    {
        Calls++;
        throw new Adapters.TranscribeTransientException("CT126 re-ASR unreachable (synthetic)");
    }
}
