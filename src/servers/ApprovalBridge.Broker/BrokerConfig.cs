using Sinapsi.Nats;

namespace ApprovalBridge.Broker;

/// <summary>
/// Env-driven configuration for the broker. The default posture is SHADOW / deny-by-default:
/// dispatch is always the C2 <c>NullActCommandDispatcher</c> in E1.3 (no executor exists), and live
/// approve-channel authz (E1.5) is deferred, so the service runs but acts on nothing. Nothing here can
/// turn dispatch live — that is a later, operator-gated trust-boundary flip (docs/66 §10).
/// </summary>
internal sealed record BrokerConfig
{
    public const string Version = "0.1.0-shadow";

    /// <summary>JetStream KV bucket holding pending approvals (docs/66 §3.1).</summary>
    public string KvBucket { get; init; } = "APPROVAL_REQUESTS";

    /// <summary>Directory of git-backed allowlist YAML (E1.1). Empty ⇒ start with an empty allowlist
    /// (deny everything) — legitimate for a dormant shadow deploy.</summary>
    public string ActionsDir { get; init; } = string.Empty;

    /// <summary>CloudEvents producer source URI for emitted facts.</summary>
    public string EventSource { get; init; } = "approval-bridge-broker://shadow";

    /// <summary>When true (default), use the in-memory store + logging emitter and never touch NATS —
    /// the fully dormant posture. Set false only once a durable bus + KV are provisioned.</summary>
    public bool ShadowLocalOnly { get; init; } = true;

    /// <summary>When true, bind <c>IActCommandDispatcher</c> to the real target-side E1.4
    /// <c>ExecutorDispatcher</c> instead of the C2 <c>NullActCommandDispatcher</c>. DEFAULT FALSE —
    /// the dormant / deny-by-default posture. Flipping this to true is a trust-boundary flip
    /// (always-escalate, docs/66 §10; <c>CLAUDE.md</c> rule 7/8) and is out of scope for E1.4.</summary>
    public bool ExecutorLive { get; init; }

    /// <summary>Root directory of per-target-identity Path-D secret files, used only when
    /// <see cref="ExecutorLive"/> is true (<c>&lt;root&gt;/&lt;target-identity&gt;/&lt;SECRET_NAME&gt;</c>, 0600).</summary>
    public string ExecutorSecretsRoot { get; init; } = string.Empty;

    public NatsConnectionOptions Nats { get; init; } = new();

    public static BrokerConfig FromEnvironment() => new()
    {
        KvBucket = Env("BRIDGE_KV_BUCKET") ?? "APPROVAL_REQUESTS",
        ActionsDir = Env("BRIDGE_ACTIONS_DIR") ?? string.Empty,
        EventSource = Env("BRIDGE_EVENT_SOURCE") ?? "approval-bridge-broker://shadow",
        ShadowLocalOnly = (Env("BRIDGE_SHADOW_LOCAL_ONLY") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
        ExecutorLive = (Env("BRIDGE_EXECUTOR_LIVE") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase),
        ExecutorSecretsRoot = Env("BRIDGE_EXECUTOR_SECRETS_ROOT") ?? string.Empty,
        Nats = NatsConnectionOptions.FromEnvironment(),
    };

    private static string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
}
