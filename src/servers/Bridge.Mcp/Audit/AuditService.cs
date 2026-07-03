using System.Security.Cryptography;
using System.Text;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;

namespace Bridge.Mcp.Audit;

/// <summary>
/// Audit appender — batched background writer to AUDIT_REPO audit-YYYY-MM.md.
/// Exact port of the Python audit worker in main.py.
///
/// Format: 8-column pipe-table:
///   | timestamp | tool | subject | project | query | sensitive | source_ip | outcome |
///
/// Sensitive-query redaction: sha256(query)[:16] prefixed "sha256:" (matches Python).
/// Batching: entries are queued fire-and-forget; background thread drains
/// every ~60s (or sooner when at least one entry arrives), matching the Python
/// daemon worker design.
/// </summary>
public sealed class AuditService : IHostedService, IDisposable
{
    private record AuditEntry(
        string Timestamp,
        string Tool,
        string Subject,
        string? Project,
        string? Query,
        bool Sensitive,
        string SourceIp,
        string Outcome = "ok");

    private readonly BridgeConfig _config;
    private readonly ILogger<AuditService> _logger;
    // GitOpsService injected lazily to avoid circular DI; resolved from DI root.
    private GitOpsService? _git;
    private readonly System.Collections.Concurrent.BlockingCollection<AuditEntry> _queue
        = new(boundedCapacity: 10_000);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public AuditService(BridgeConfig config, ILogger<AuditService> logger)
    {
        _config = config;
        _logger = logger;
    }

    // Inject GitOpsService after construction (set by Program.cs / DI).
    public void SetGitOps(GitOpsService git) => _git = git;

    public Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoop(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _queue.CompleteAdding();
        _cts?.Cancel();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { /* graceful drain timeout */ }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _queue.Dispose();
    }

    // ── Public enqueue ────────────────────────────────────────────────────────

    public void Enqueue(
        string tool,
        BridgeAuthContext? ctx,
        string? project = null,
        string? query = null,
        bool sensitive = false,
        string outcome = "ok")
    {
        var subject = ctx?.Subject ?? "<unauthenticated>";
        string? recordedQuery = query;
        if (sensitive && query is not null)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(query));
            recordedQuery = "sha256:" + Convert.ToHexString(digest).ToLowerInvariant()[..16];
        }

        var entry = new AuditEntry(
            Timestamp : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Tool      : tool,
            Subject   : subject,
            Project   : project,
            Query     : recordedQuery,
            Sensitive : sensitive,
            SourceIp  : BridgeAuthState.CurrentSourceIp,
            Outcome   : outcome);

        // Fire-and-forget; don't block the request.
        if (!_queue.TryAdd(entry))
            _logger.LogWarning("Audit queue full; dropping entry for tool={Tool}", tool);
    }

    // ── Background worker ─────────────────────────────────────────────────────

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Wait up to 60s for at least one entry (mirrors Python Queue.get(timeout=60)).
            AuditEntry? first = null;
            try
            {
                // TryTake with timeout (ms); returns false on timeout or cancellation.
                if (!_queue.TryTake(out first, millisecondsTimeout: 60_000, ct))
                    continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var batch = new List<AuditEntry> { first! };

            // Drain anything else queued in the next ~5s (matches Python's drain window).
            var drainUntil = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < drainUntil && !ct.IsCancellationRequested)
            {
                if (!_queue.TryTake(out var next, millisecondsTimeout: 250, ct))
                    break;
                batch.Add(next!);
            }

            await FlushBatchAsync(batch, ct);
        }

        // Drain remaining entries on shutdown.
        var remaining = new List<AuditEntry>();
        while (_queue.TryTake(out var e))
            remaining.Add(e);
        if (remaining.Count > 0)
            await FlushBatchAsync(remaining, CancellationToken.None);
    }

    private async Task FlushBatchAsync(List<AuditEntry> batch, CancellationToken ct)
    {
        if (_git is null)
        {
            _logger.LogWarning("GitOpsService not set; dropping audit batch of {Count}", batch.Count);
            return;
        }

        // Group by month.
        var perMonth = batch
            .GroupBy(e => e.Timestamp[..7]) // YYYY-MM
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (month, entries) in perMonth)
        {
            var path = $"audit-{month}.md";
            string existing;
            try
            {
                existing = await _git.ReadFileRawAsync(_config.AuditRepo, path, ct);
            }
            catch (FileNotFoundException)
            {
                existing = $"# Audit log {month}\n\n"
                    + "| timestamp | tool | subject | project | query | sensitive | source_ip | outcome |\n"
                    + "|---|---|---|---|---|---|---|---|\n";
            }
            catch (Exception ex)
            {
                _logger.LogWarning("audit fetch failed: {Message}", ex.Message);
                existing = $"# Audit log {month}\n\n"
                    + "| timestamp | tool | subject | project | query | sensitive | source_ip | outcome |\n"
                    + "|---|---|---|---|---|---|---|---|\n";
            }

            var lines = entries.Select(e =>
                $"| {e.Timestamp} | {e.Tool} | {e.Subject} | {e.Project ?? "-"} "
                + $"| {(e.Query ?? "-").Replace("|", "\\|")} | {(e.Sensitive ? "yes" : "no")} "
                + $"| {e.SourceIp} | {e.Outcome} |");

            var updated = existing.TrimEnd() + "\n" + string.Join("\n", lines) + "\n";

            try
            {
                await _git.CommitToRepoAsync(
                    _config.AuditRepo,
                    path,
                    updated,
                    $"audit: +{entries.Count} entries",
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError("audit commit failed: {Message}", ex.Message);
            }
        }
    }
}
