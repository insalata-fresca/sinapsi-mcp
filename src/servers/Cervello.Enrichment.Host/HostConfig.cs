namespace Cervello.Enrichment.Host;

/// <summary>
/// Drain-loop + health knobs for the enrichment host (env-driven, fail-closed — mirrors
/// <c>Cervello.Watcher.WatcherConfig</c>). This carries ONLY the host's own operational knobs (poll
/// interval, batch size, health bind); the ENGINE's config (live-vs-fake, phase gate, endpoints,
/// DSN) is <c>Cervello.Enrichment.EnrichmentConfig</c>, loaded separately and handed to
/// <c>AddCervelloEnrichment</c>. A bad numeric/range value throws at startup naming the offending env
/// var rather than silently honouring a footgun. No host, path, or credential is baked into source.
/// </summary>
public sealed record HostConfig
{
    /// <summary>Poll interval (seconds) between drain cycles. Default ~30 s (fail-closed 5..3600).</summary>
    public required int PollIntervalSeconds { get; init; }

    /// <summary>Max recordings leased per drain cycle (fail-closed 1..1000). Default 16.</summary>
    public required int BatchSize { get; init; }

    /// <summary>Bind host for the opaque health endpoint.</summary>
    public required string HealthHost { get; init; }

    /// <summary>Bind port for the opaque health endpoint (fail-closed 1..65535). Default 8147.</summary>
    public required int HealthPort { get; init; }

    internal const int DefaultPollIntervalSeconds = 30;
    internal const int MinPollIntervalSeconds = 5;
    internal const int MaxPollIntervalSeconds = 3_600;

    internal const int DefaultBatchSize = 16;

    // 8147: one above the Watcher's 8146 — the two cervello workers co-reside on CT146-cervello.
    internal const int DefaultHealthPort = 8147;

    /// <summary>Read config from the process environment (production path).</summary>
    public static HostConfig FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// <summary>Read config from an INJECTABLE env source (test-isolation).</summary>
    public static HostConfig From(Func<string, string?> getEnv)
    {
        string Env(string k, string dflt) => getEnv(k) is { Length: > 0 } v ? v : dflt;

        return new HostConfig
        {
            PollIntervalSeconds = ReadBoundedInt(getEnv,
                "CERVELLO_ENRICHMENT_POLL_INTERVAL_SECONDS", DefaultPollIntervalSeconds,
                MinPollIntervalSeconds, MaxPollIntervalSeconds),
            BatchSize = ReadBoundedInt(getEnv, "CERVELLO_ENRICHMENT_BATCH_SIZE", DefaultBatchSize, 1, 1_000),
            HealthHost = Env("CERVELLO_ENRICHMENT_HEALTH_HOST", "0.0.0.0"),
            HealthPort = ReadBoundedInt(getEnv, "CERVELLO_ENRICHMENT_HEALTH_PORT", DefaultHealthPort, 1, 65_535),
        };
    }

    /// <summary>Convenience overload: read from a LOCAL dictionary (tests).</summary>
    public static HostConfig From(IReadOnlyDictionary<string, string?> env) =>
        From(k => env.TryGetValue(k, out var v) ? v : null);

    private static int ReadBoundedInt(Func<string, string?> getEnv, string envVar, int dflt, int min, int max)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrEmpty(raw))
            return dflt;
        if (!int.TryParse(raw, out var v) || v < min || v > max)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in {min}..{max} (default {dflt}).");
        return v;
    }
}
