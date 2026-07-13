using System.Text.Json.Nodes;
using Sinapsi.Nats;

namespace Sshgw.Mcp;

/// <summary>
/// Emits every <c>execute-command</c> authorization decision (Q2 of the agent
/// authorization plane — see home-server <c>docs/61</c>) onto the NATS bus in a
/// common envelope, so the control plane (Sinapsi Sentinel) can show the
/// command-safety layer live alongside Q1 (gateway/OpenFGA) and Q3 (harness
/// gates). Without this the command layer is invisible — the exact gap that made
/// the authorization posture impossible to see without an agent session.
///
/// <para>DISCIPLINE — emission never affects the tool call. It is:</para>
/// <list type="bullet">
///   <item><b>optional</b> — if no NATS identity is configured (<see cref="FromEnvironmentOrNull"/>
///     returns null), it is a no-op and the MCP runs exactly as before;</item>
///   <item><b>fire-and-forget</b> — <see cref="Emit"/> returns immediately; the publish
///     runs detached on the singleton's held connection and never blocks or fails the
///     command (best-effort, same posture as the harness security-audit spool);</item>
///   <item><b>fail-open</b> — any connect/publish error is swallowed; a decision that
///     cannot be published is simply not shown, never an exception into the request.</item>
/// </list>
///
/// <para>CORRELATION — the envelope carries a <c>correlation_id</c> so a decision can be
/// joined to the same request's Q1/Q3 decisions into one per-request chain. Today the MCP
/// has no shared trace id threaded from the harness/gateway, so it emits a per-decision id;
/// threading ONE id through harness → gateway → MCP is the follow-up that lights up the full
/// cross-layer chain (Scope 50 phase 6). Emitting the decision at all is the first brick.</para>
/// </summary>
public sealed class AuthzDecisionPublisher : IAsyncDisposable
{
    // Joins the homelab.security.> family the Sentinel already consumes, alongside the
    // harness homelab.security.ask-gate.* / deny-floor.* (Q3). Subject:
    //   homelab.security.authz.q2.<verdict>.cse
    internal const string SubjectBase = "homelab.security.authz.q2";
    internal const string Surface = "cse";
    private const int MaxCommandChars = 240;

    private readonly NatsConnectionOptions _opts;
    private readonly string _eventSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NatsEventPublisher? _pub;

    private AuthzDecisionPublisher(NatsConnectionOptions opts, string eventSource)
    {
        _opts = opts;
        _eventSource = eventSource;
    }

    /// <summary>
    /// Build a publisher from the environment, or return <c>null</c> when emission is not
    /// configured (no scoped NATS identity) — in which case the MCP simply does not emit.
    /// Uses a SCOPED publish-only identity (<c>SSHGW_AUTHZ_NATS_SEED_PATH</c> /
    /// <c>SSHGW_AUTHZ_NATS_NKEY</c>) authorised only on <c>homelab.security.authz.&gt;</c> —
    /// never a broad admin nkey.
    /// </summary>
    public static AuthzDecisionPublisher? FromEnvironmentOrNull()
    {
        var seed = Environment.GetEnvironmentVariable("SSHGW_AUTHZ_NATS_SEED_PATH");
        var nkey = Environment.GetEnvironmentVariable("SSHGW_AUTHZ_NATS_NKEY");
        // Emission is opt-in: without a scoped publish identity we stay a no-op so a
        // deploy that has not provisioned the identity yet runs unchanged.
        if (string.IsNullOrWhiteSpace(seed) || string.IsNullOrWhiteSpace(nkey))
            return null;

        var baseOpts = NatsConnectionOptions.FromEnvironment() with
        {
            ClientName = "sshgw-mcp-authz",
            NKeySeedPath = seed,
            NKeyPublic = nkey,
        };
        var source = Environment.GetEnvironmentVariable("SSHGW_AUTHZ_EVENT_SOURCE") is { Length: > 0 } s
            ? s : "sshgw-mcp://authz/";
        return new AuthzDecisionPublisher(baseOpts, source);
    }

    /// <summary>Fire-and-forget: hand the decision to a detached publish and return at once.
    /// Never throws, never blocks the command.</summary>
    public void Emit(
        string verdict, string reason, string? server, string command, string? correlationId)
    {
        var data = BuildEnvelope(verdict, reason, server, command, correlationId);
        var verdictSlug = Slug(verdict);
        _ = PublishSafeAsync($"{SubjectBase}.{verdictSlug}.{Surface}", data);
    }

    /// <summary>The common decision-envelope payload (pure — unit-tested without a broker).
    /// The command is truncated + secret-scrubbed: a DENY carries the offending path, which
    /// is useful audit context, but never raw secret VALUES (scrubbed via
    /// <see cref="SshgwErrors.Sanitize"/>).</summary>
    internal static JsonObject BuildEnvelope(
        string verdict, string reason, string? server, string command, string? correlationId)
    {
        var scrubbed = SshgwErrors.Sanitize(command ?? "") ?? "";
        if (scrubbed.Length > MaxCommandChars)
            scrubbed = scrubbed[..MaxCommandChars] + "…";
        var verb = FirstToken(command ?? "");

        return new JsonObject
        {
            ["layer"] = "q2",
            ["question"] = "command-safety",
            ["surface"] = "in-mcp-authorizer",
            ["tool"] = "sshgw.execute-command",
            ["server"] = server ?? "",
            ["verb"] = verb,
            ["verdict"] = verdict,       // allow | requiresApproval | deny
            ["reason"] = reason ?? "",
            ["command"] = scrubbed,
            // Present so the read-model can join Q1/Q2/Q3 for one request. Until a shared
            // trace id is threaded from the harness/gateway, this is per-decision.
            ["correlation_id"] = correlationId ?? Guid.NewGuid().ToString("D"),
        };
    }

    private async Task PublishSafeAsync(string subject, JsonObject data)
    {
        try
        {
            var pub = await GetAsync(CancellationToken.None);
            await pub.PublishAsync(subject, data);
        }
        catch
        {
            // fail-open: a decision that cannot be published is simply not shown.
        }
    }

    private async ValueTask<NatsEventPublisher> GetAsync(CancellationToken ct)
    {
        if (_pub is not null) return _pub;
        await _gate.WaitAsync(ct);
        try
        {
            _pub ??= await NatsEventPublisher.ConnectAsync(_opts, _eventSource, ct);
        }
        finally
        {
            _gate.Release();
        }
        return _pub;
    }

    private static string FirstToken(string cmd)
    {
        var t = cmd.TrimStart();
        int i = t.IndexOfAny(new[] { ' ', '\t' });
        var head = i < 0 ? t : t[..i];
        int slash = head.LastIndexOf('/');
        return slash >= 0 && slash + 1 < head.Length ? head[(slash + 1)..] : head;
    }

    // subject tokens must be metachar-free; map the camelCase verdict to a safe slug.
    private static string Slug(string verdict) => verdict switch
    {
        "requiresApproval" => "requires-approval",
        _ => verdict,
    };

    public async ValueTask DisposeAsync()
    {
        if (_pub is not null) await _pub.DisposeAsync();
        _gate.Dispose();
    }
}
