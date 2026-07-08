using System.Text;
using System.Text.Json;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT-side <see cref="IAccessLog"/> (spec <c>open-points-mcp</c> → "Calls are scoped and
/// logged"; DESIGN §2.3). EVERY open-points tool call is appended as one redacted JSON line to a
/// CT-local append-only log file — tool name, caller scope, the point id (if any), the outcome, and
/// a UTC timestamp. Entries carry NO body / audio / vector (R10): the <see cref="AccessLogEntry"/>
/// type only exposes those redacted fields. Append is atomic (a single <c>File.AppendAllText</c> of
/// one line), and the directory is created on first write.
/// </summary>
public sealed class CtAccessLog : IAccessLog
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly string _logPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CtAccessLog(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            throw new ArgumentException("logPath must be non-empty", nameof(logPath));
        _logPath = logPath;
    }

    public async Task AppendAsync(AccessLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Serialize only the redacted fields (the record has no body/audio/vector — R10 by type).
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            tool = entry.Tool,
            scope = entry.Scope,
            outcome = entry.Outcome,
            point_id = entry.PointId,
        }, _json) + "\n";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_logPath, line, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
