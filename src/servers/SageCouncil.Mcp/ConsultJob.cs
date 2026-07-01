using System.Collections.Concurrent;
using System.Text.Json;

namespace SageCouncil.Mcp;

/// <summary>
/// A single in-flight (or completed) council consult. A consult can run for
/// many minutes by design — members do genuine multi-step research — so it is
/// dispatched as a detached background job and polled via <c>consult_result</c>,
/// rather than blocking the inbound MCP request. All mutating transitions are
/// guarded so the polling reader sees a consistent snapshot.
/// </summary>
public sealed class ConsultJob
{
    private readonly object _gate = new();
    private string _state = "running";
    private string? _result;
    private string? _error;
    private DateTimeOffset? _completedAt;

    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public required string Focus { get; init; }
    public required IReadOnlyList<string> Members { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void Complete(string result)
    {
        lock (_gate) { _state = "done"; _result = result; _completedAt = DateTimeOffset.UtcNow; }
    }

    public void Fail(string error)
    {
        lock (_gate) { _state = "error"; _error = error; _completedAt = DateTimeOffset.UtcNow; }
    }

    /// <summary>Serialised status/result for the <c>consult_result</c> poll.</summary>
    public string Snapshot(JsonSerializerOptions json)
    {
        lock (_gate)
        {
            var elapsedMs = (long)((_completedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalMilliseconds;
            return JsonSerializer.Serialize(new
            {
                job_id = Id,
                status = _state,
                focus = Focus,
                members = Members,
                started_at = StartedAt,
                completed_at = _completedAt,
                elapsed_ms = elapsedMs,
                // When done, `result` carries the full council JSON (members + synthesis).
                result = _state == "done" && _result is not null
                    ? JsonSerializer.Deserialize<JsonElement>(_result)
                    : (JsonElement?)null,
                error = _error,
            }, json);
        }
    }
}

/// <summary>
/// In-memory registry of council jobs. <see cref="Start"/> dispatches the work
/// on a detached task driven by a job-scoped cancellation token — deliberately
/// NOT the inbound request token — so the consult survives after <c>consult</c>
/// returns the job id. Completed jobs are evicted after a TTL.
/// </summary>
public sealed class ConsultJobStore(IServiceScopeFactory scopes, CouncilOptions opt, ILogger<ConsultJobStore> log)
{
    private static readonly TimeSpan _retention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, ConsultJob> _jobs = new(StringComparer.Ordinal);

    public ConsultJob Start(string prompt, string focus, IReadOnlyList<string> members)
    {
        var job = new ConsultJob
        {
            Id = "council-" + Guid.NewGuid().ToString("N")[..12],
            Prompt = prompt,
            Focus = focus,
            Members = members,
        };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    public ConsultJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    private async Task RunAsync(ConsultJob job)
    {
        // Members fan out in parallel (capped at MemberDeadline each), then one
        // synthesis call (capped at Timeout). Give the whole job that headroom
        // plus slack rather than letting it run unbounded.
        var ceiling = opt.MemberDeadline + opt.Timeout + TimeSpan.FromMinutes(2);
        using var cts = new CancellationTokenSource(ceiling);
        try
        {
            using var scope = scopes.CreateScope();
            var council = scope.ServiceProvider.GetRequiredService<CouncilService>();
            log.LogInformation("consult job {Id} started ({Members}, focus={Focus})",
                job.Id, string.Join(",", job.Members), job.Focus);
            var result = await council.ConsultAsync(job.Prompt, job.Focus, job.Members, cts.Token).ConfigureAwait(false);
            job.Complete(result);
            log.LogInformation("consult job {Id} done", job.Id);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            job.Fail($"consult exceeded overall ceiling ({ceiling.TotalMinutes:0} min)");
            log.LogWarning("consult job {Id} hit overall ceiling", job.Id);
        }
        catch (Exception e)
        {
            // Sanitize the surfaced failure text: an exception message could carry
            // an upstream credential/token. The overall-ceiling message above is
            // server-authored and secret-free, so only this arbitrary-exception
            // leg needs redacting.
            job.Fail(CouncilErrors.Sanitize(e.Message));
            log.LogError(e, "consult job {Id} failed", job.Id);
        }
        finally
        {
            _ = EvictLaterAsync(job.Id);
        }
    }

    private async Task EvictLaterAsync(string id)
    {
        await Task.Delay(_retention).ConfigureAwait(false);
        _jobs.TryRemove(id, out _);
    }
}
