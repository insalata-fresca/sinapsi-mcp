using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// The operator-facing projection of one open request, mirroring the broker's
/// <c>PendingApprovalView</c>: the registry <see cref="Title"/> + the
/// TYPED <see cref="Params"/> + provenance (<see cref="RequesterIdentity"/>, <see cref="ActionId"/>,
/// <see cref="ExpiresAt"/>). There is no free-text-rationale field here, or anywhere upstream of it —
/// the executor and the audit trail only ever see <c>action_id</c> + schema-validated params.
/// </summary>
public sealed record PendingApprovalDto(
    string RequestId,
    string ActionId,
    string Title,
    string Description,
    JsonElement? Params,
    string RequesterIdentity,
    DateTimeOffset ExpiresAt,
    string RiskTier);

/// <summary>The pending queue as read from the broker, plus whether the broker was reachable — the
/// Console fails visibly-but-safely (an empty queue + a "broker unreachable" flag), never a crash.</summary>
public sealed record PendingQueueResult(bool BrokerReachable, IReadOnlyList<PendingApprovalDto> Items)
{
    public static PendingQueueResult Unreachable() => new(false, Array.Empty<PendingApprovalDto>());
    public static PendingQueueResult Ok(IReadOnlyList<PendingApprovalDto> items) => new(true, items);
}

/// <summary>The broker's raw response to an approve/reject COMMAND, forwarded verbatim to the Console's
/// own caller — the Console does not reinterpret or soften the broker's verdict, it is a transparent
/// relay (the actual one-shot / self-approval / CAS enforcement lives entirely in the broker).</summary>
public sealed record ApprovalCommandResult(bool Reached, int StatusCode, string RawBody)
{
    public static ApprovalCommandResult Unreachable() => new(false, 0, "");
}

/// <summary>
/// The Console's ONLY path to the Operator Approval Bridge broker's command API: a thin,
/// transparent HTTP client. The Console never re-implements, shortcuts, or bypasses the broker's
/// server-side one-shot / approver≠requester / CAS checks — every Approve/Reject click is forwarded
/// verbatim to the broker's <c>POST /approve</c> / <c>POST /reject</c>, and the broker's own response
/// (accepted, or a structured rejection with its reason) is what the operator sees. The pending-queue
/// read (<see cref="GetPendingAsync"/>) is likewise a pure proxy to the broker's READ-ONLY
/// <c>GET /pending</c> — it performs no state transition.
///
/// <para>Configured via <c>BRIDGE_BROKER_URL</c> (the <see cref="HttpClient.BaseAddress"/>). Every call
/// fails soft: an unreachable broker degrades the Console's approval lane to "broker unreachable" (like
/// <see cref="SecurityBusSubscriber.Connected"/> does for the bus), never a 500 that takes the whole
/// Console down.</para>
/// </summary>
public sealed class BrokerClient
{
    private static readonly JsonSerializerOptions ReadOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly ILogger<BrokerClient> _log;

    public BrokerClient(HttpClient http, ILogger<BrokerClient> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>READ-ONLY: the currently-open requests, title + typed params + provenance, soonest-
    /// expiring first (as the broker orders them). Never mutates broker state.</summary>
    public async Task<PendingQueueResult> GetPendingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/pending", ct);
            if (!resp.IsSuccessStatusCode) return PendingQueueResult.Unreachable();
            var items = await resp.Content.ReadFromJsonAsync<List<PendingApprovalDto>>(ReadOpts, ct);
            return PendingQueueResult.Ok(items ?? []);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "approval-bridge broker GET /pending unreachable");
            return PendingQueueResult.Unreachable();
        }
    }

    /// <summary>Forward an operator Approve click to the broker's COMMAND endpoint (single receiver,
    /// rejectable). <paramref name="approverIdentity"/> is never the requester's identity
    /// by construction on this path; the broker still re-checks it regardless.</summary>
    public Task<ApprovalCommandResult> ApproveAsync(string requestId, string approverIdentity, CancellationToken ct = default)
        => PostCommandAsync("/approve", requestId, approverIdentity, ct);

    /// <summary>Forward an operator Reject click to the broker's COMMAND endpoint.</summary>
    public Task<ApprovalCommandResult> RejectAsync(string requestId, string approverIdentity, CancellationToken ct = default)
        => PostCommandAsync("/reject", requestId, approverIdentity, ct);

    private async Task<ApprovalCommandResult> PostCommandAsync(string path, string requestId, string approverIdentity, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                path, new { request_id = requestId, approver_identity = approverIdentity }, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new ApprovalCommandResult(true, (int)resp.StatusCode, body);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "approval-bridge broker POST {Path} unreachable", path);
            return ApprovalCommandResult.Unreachable();
        }
    }
}
