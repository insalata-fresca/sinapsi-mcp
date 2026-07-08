namespace Cervello.Enrichment.Ports;

/// <summary>
/// The cervello access log (spec <c>open-points-mcp</c> → "Calls are scoped and logged"; DESIGN
/// §2.3). EVERY open-points tool call is appended here — tool name, caller scope, the point id (if
/// any), and the outcome — so the private-plane surface is auditable. Entries carry NO body / audio
/// / vector (R10). In prod this writes the CT-side access log; a fake collects entries in tests.
/// </summary>
public interface IAccessLog
{
    Task AppendAsync(AccessLogEntry entry, CancellationToken ct = default);
}

/// <summary>One redacted access-log line for an open-points tool call.</summary>
public sealed record AccessLogEntry(string Tool, string Scope, string Outcome, string? PointId = null);
