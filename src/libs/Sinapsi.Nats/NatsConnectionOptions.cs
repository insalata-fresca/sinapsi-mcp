using NATS.Client.Core;

namespace Sinapsi.Nats;

/// <summary>
/// Connection settings for a NATS client: NKey-seed auth + pinned-CA TLS.
/// Every field has an env-var binding (see <see cref="FromEnvironment"/>) so a
/// service is configured entirely from its environment. The opt-in
/// <c>NATS_TLS_DISABLE</c> knob lets a service connect to a plaintext (no-TLS)
/// bus while keeping NKey auth — useful for ephemeral local/test buses.
/// </summary>
public sealed record NatsConnectionOptions
{
    /// <summary>NATS server URL. Override via <c>NATS_URL</c>.</summary>
    public string Url { get; init; } = "nats://127.0.0.1:4222";
    /// <summary>Path to the file holding the NKey seed (<c>S...</c>). Override via <c>NATS_NKEY_SEED_PATH</c>.</summary>
    public string NKeySeedPath { get; init; } = "nats.seed";
    /// <summary>Public NKey (<c>U...</c>). NATS.Net requires both the public key and the
    /// seed for nkey auth — the seed alone is rejected. Non-secret; supplied via
    /// the <c>NATS_NKEY</c> env var. Leave unset to skip NKey auth.</summary>
    public string? NKeyPublic { get; init; }
    /// <summary>Pinned CA certificate file for TLS verification. Override via <c>NATS_TLS_CA_FILE</c>.
    /// Empty/unset → TLS with the system trust store (<c>TlsMode.Auto</c>, no pinned CA).</summary>
    public string? TlsCaFile { get; init; }
    /// <summary>Opt-in: connect to a PLAINTEXT (no-TLS) NATS server. When true,
    /// <see cref="BuildNatsOpts"/> sets <c>TlsMode.Disable</c> and omits <c>CaFile</c>
    /// entirely, regardless of <see cref="TlsCaFile"/>. NKey-seed auth still applies
    /// (nkey works over plaintext). Supplied via the <c>NATS_TLS_DISABLE</c> env var
    /// (truthy = <c>1</c>/<c>true</c>). Default false = TLS behaviour.
    /// Useful for an ephemeral no-TLS test bus: the default <c>{CaFile set, TlsMode.Auto}</c>
    /// forces a TLS handshake (NATS.Net 2.8.0), so this is the env-level way to say
    /// "no TLS / no CA".</summary>
    public bool TlsDisable { get; init; }
    /// <summary>Client name reported to the server. Override via <c>NATS_CLIENT_NAME</c>.</summary>
    public string ClientName { get; init; } = "sinapsi-nats";
    /// <summary>Time (ms) the client waits for a TCP+auth connect before giving up.
    /// Override via <c>NATS_CONNECT_TIMEOUT_MS</c>. Bounds an otherwise-unbounded
    /// connect so a wrong URL / unreachable bus fails fast instead of hanging.
    /// A non-numeric, <c>&lt;= 0</c>, or above-<see cref="MaxConnectTimeoutMs"/>
    /// value is rejected as invalid config (fail-closed) — see
    /// <see cref="FromEnvironment"/>.</summary>
    public int ConnectTimeoutMs { get; init; } = DefaultConnectTimeoutMs;

    /// <summary>Default connect timeout (ms) when <c>NATS_CONNECT_TIMEOUT_MS</c> is unset.</summary>
    internal const int DefaultConnectTimeoutMs = 10_000;

    /// <summary>Upper bound on a configurable connect timeout (ms). Two minutes is
    /// far past any legitimate connect; a larger value is treated as a config error,
    /// not honoured.</summary>
    internal const int MaxConnectTimeoutMs = 120_000;

    /// <summary>Build options from the process environment. Neutral defaults let the
    /// call succeed with no env vars set (against a local plaintext bus); a *malformed*
    /// value fails closed with an error naming the offending env var. The connect
    /// timeout is validated here so bad config is rejected at bind time, not at connect.</summary>
    public static NatsConnectionOptions FromEnvironment()
    {
        var opts = new NatsConnectionOptions
        {
            Url = Environment.GetEnvironmentVariable("NATS_URL") is { Length: > 0 } u ? u : "nats://127.0.0.1:4222",
            NKeySeedPath = Environment.GetEnvironmentVariable("NATS_NKEY_SEED_PATH") is { Length: > 0 } sp ? sp : "nats.seed",
            NKeyPublic = Environment.GetEnvironmentVariable("NATS_NKEY"),
            TlsCaFile = Environment.GetEnvironmentVariable("NATS_TLS_CA_FILE") is { Length: > 0 } ca ? ca : null,
            TlsDisable = IsTruthy(Environment.GetEnvironmentVariable("NATS_TLS_DISABLE")),
            ClientName = Environment.GetEnvironmentVariable("NATS_CLIENT_NAME") is { Length: > 0 } cn ? cn : "sinapsi-nats",
            ConnectTimeoutMs = ReadConnectTimeoutMs(),
        };
        // Fail-closed: reject a malformed explicit URL / NKey at bind time, naming the var.
        opts.Validate();
        return opts;
    }

    private static bool IsTruthy(string? v) =>
        v is "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Read + clamp <c>NATS_CONNECT_TIMEOUT_MS</c>. Fail-closed: a value of <c>0</c>
    /// would make every connect time out instantly and a negative value throws inside
    /// the underlying <see cref="TimeSpan"/> use; either — and any value above
    /// <see cref="MaxConnectTimeoutMs"/> or non-numeric — is rejected with a clear
    /// error naming the offending env var rather than silently honouring a footgun.
    /// </summary>
    private static int ReadConnectTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("NATS_CONNECT_TIMEOUT_MS");
        if (string.IsNullOrEmpty(raw))
            return DefaultConnectTimeoutMs;
        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxConnectTimeoutMs)
            throw new InvalidOperationException(
                $"NATS_CONNECT_TIMEOUT_MS='{raw}' is invalid: expected an integer in " +
                $"1..{MaxConnectTimeoutMs} ms (default {DefaultConnectTimeoutMs}).");
        return ms;
    }

    /// <summary>
    /// Fail-closed validation of the public inputs this record carries. Throws an
    /// <see cref="InvalidOperationException"/> naming the offending value when the URL
    /// is missing/malformed, the public NKey is malformed, or the connect timeout is
    /// out of range. Called automatically by <see cref="FromEnvironment"/> and
    /// <see cref="BuildNatsOpts"/> so a bad value never reaches the NATS client.
    /// </summary>
    public void Validate()
    {
        if (NatsValidation.ValidateUrl(Url) is { } urlReason)
            throw new InvalidOperationException($"NATS_URL invalid: {urlReason}");
        if (NatsValidation.ValidateNKeyPublic(NKeyPublic) is { } nkeyReason)
            throw new InvalidOperationException($"NATS_NKEY invalid: {nkeyReason}");
        if (ConnectTimeoutMs <= 0 || ConnectTimeoutMs > MaxConnectTimeoutMs)
            throw new InvalidOperationException(
                $"ConnectTimeoutMs={ConnectTimeoutMs} is invalid: expected 1..{MaxConnectTimeoutMs} ms.");
    }

    /// <summary>Build NatsOpts: NKey public+seed auth, TLS verified against the pinned CA.
    /// When <see cref="TlsDisable"/> is set, TLS is disabled (<c>TlsMode.Disable</c>, no
    /// <c>CaFile</c>) so the client connects to a plaintext bus — nkey auth is retained.</summary>
    public NatsOpts BuildNatsOpts()
    {
        // Fail-closed: a directly-constructed options record (bypassing
        // FromEnvironment) is still validated before it can reach the client.
        Validate();

        var auth = new NatsAuthOpts { NKey = NKeyPublic };
        if (!string.IsNullOrEmpty(NKeySeedPath) && File.Exists(NKeySeedPath))
            auth = auth with { Seed = File.ReadAllText(NKeySeedPath).Trim() };
        var tls = TlsDisable
            ? new NatsTlsOpts { Mode = TlsMode.Disable }
            : string.IsNullOrEmpty(TlsCaFile)
                ? new NatsTlsOpts { Mode = TlsMode.Auto }
                : new NatsTlsOpts { CaFile = TlsCaFile, Mode = TlsMode.Auto };
        return new NatsOpts
        {
            Url = Url,
            Name = ClientName,
            AuthOpts = auth,
            TlsOpts = tls,
            ConnectTimeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs),
        };
    }
}
