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

    /// <summary>Build options from the process environment. Every property has a default,
    /// so the call succeeds even with no env vars set (against a local plaintext bus).</summary>
    public static NatsConnectionOptions FromEnvironment() => new()
    {
        Url = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://127.0.0.1:4222",
        NKeySeedPath = Environment.GetEnvironmentVariable("NATS_NKEY_SEED_PATH") ?? "nats.seed",
        NKeyPublic = Environment.GetEnvironmentVariable("NATS_NKEY"),
        TlsCaFile = Environment.GetEnvironmentVariable("NATS_TLS_CA_FILE") is { Length: > 0 } ca ? ca : null,
        TlsDisable = IsTruthy(Environment.GetEnvironmentVariable("NATS_TLS_DISABLE")),
        ClientName = Environment.GetEnvironmentVariable("NATS_CLIENT_NAME") ?? "sinapsi-nats",
    };

    private static bool IsTruthy(string? v) =>
        v is "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Build NatsOpts: NKey public+seed auth, TLS verified against the pinned CA.
    /// When <see cref="TlsDisable"/> is set, TLS is disabled (<c>TlsMode.Disable</c>, no
    /// <c>CaFile</c>) so the client connects to a plaintext bus — nkey auth is retained.</summary>
    public NatsOpts BuildNatsOpts()
    {
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
        };
    }
}
