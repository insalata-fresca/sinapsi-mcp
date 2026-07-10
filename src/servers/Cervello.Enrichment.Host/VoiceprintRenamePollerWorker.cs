using Cervello.Enrichment;
using Cervello.Enrichment.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cervello.Enrichment.Host;

/// <summary>
/// The V5 rename-poller BACKGROUND worker (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V5, §6.1). A dedicated poller (NOT the recordings
/// <c>WatchWorker</c> — that diffs <c>fileId→md5</c> and can't see a rename; §1.5) that each cycle runs
/// <see cref="VoiceprintRenameResolver.RunCycleAsync"/>: list the <c>voiceprints/</c> folder, detect a
/// rename by diffing <c>fileId→name</c> against the unresolved candidate rows, enroll+move+re-attribute.
///
/// <para>Mirrors the <see cref="DrainWorker"/> shape — a <see cref="BackgroundService"/> with a bounded
/// poll cycle (<see cref="EnrichmentConfig.VoiceprintsPollSeconds"/>), a readiness flag, graceful
/// cancellation, and a per-cycle try/catch that backs off to the next interval. Opens NO NATS (invariant
/// 3): the only signal is the polled Drive listing.</para>
/// </summary>
public sealed class VoiceprintRenamePollerWorker : BackgroundService
{
    private readonly EnrichmentConfig _cfg;
    private readonly VoiceprintRenameResolver _resolver;
    private readonly ILogger<VoiceprintRenamePollerWorker> _log;

    public bool Ready { get; private set; }
    public long CyclesRun { get; private set; }
    public long RenamesResolved { get; private set; }

    public VoiceprintRenamePollerWorker(
        EnrichmentConfig cfg, VoiceprintRenameResolver resolver, ILogger<VoiceprintRenamePollerWorker> log)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("cervello voiceprint-rename poller starting (interval {Interval}s)", _cfg.VoiceprintsPollSeconds);
        Ready = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _resolver.RunCycleAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct).ConfigureAwait(false);
                CyclesRun++;
                RenamesResolved += result.ResolvedSlugs.Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.LogError(e, "voiceprint-rename poll cycle failed; retrying next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.VoiceprintsPollSeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
