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
