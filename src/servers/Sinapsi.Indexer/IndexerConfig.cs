// ---------------------------------------------------------------------------
// IndexerConfig - fail-closed parsing of the indexer's numeric env knobs.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Globalization;

namespace Sinapsi.Indexer;

/// <summary>
/// Fail-closed reader for the indexer's numeric configuration knobs. Every knob
/// has a sane default, a hard floor, and a hard ceiling; a value that is
/// non-numeric, below the floor, or above the ceiling THROWS an
/// <see cref="InvalidOperationException"/> naming the offending env var, so the
/// service fails to start on bad config rather than silently honouring a footgun
/// (e.g. a debounce of 0 spinning the coalesce loop, or a health port outside
/// 1..65535 that Kestrel would reject opaquely at bind time).
///
/// <para>
/// This replaces the previous scattered <c>int.TryParse(...) ? Math.Max(floor, v)
/// : default</c> pattern, which silently clamped an out-of-range value and
/// silently fell back to the default on garbage input. The numeric ranges and
/// defaults are otherwise identical, so a valid configuration behaves exactly as
/// before; only invalid configuration changes from "silently repaired" to
/// "rejected at startup with a named error".
/// </para>
/// </summary>
internal static class IndexerConfig
{
    // --- rescan / debounce (IndexerWorker) ---
    internal const int DefaultRescanIntervalMin = 60;
    internal const int MinRescanIntervalMin = 5;
    internal const int MaxRescanIntervalMin = 1_440;   // 24h ceiling.

    internal const int DefaultDebounceSec = 15;
    internal const int MinDebounceSec = 2;
    internal const int MaxDebounceSec = 3_600;         // 1h ceiling.

    // --- embed loop throttles (IndexerWorker) ---
    internal const int DefaultEmbedIdleSec = 30;
    internal const int MinEmbedIdleSec = 5;
    internal const int MaxEmbedIdleSec = 3_600;

    internal const int DefaultEmbedThrottleMs = 50;
    internal const int MinEmbedThrottleMs = 0;
    internal const int MaxEmbedThrottleMs = 60_000;

    // --- health listen port (Program.cs) ---
    internal const int DefaultHealthPort = 8009;
    internal const int MinHealthPort = 1;
    internal const int MaxHealthPort = 65_535;

    // --- embedder (OnnxEmbedder) ---
    internal const int DefaultEmbedMaxTokens = 256;
    internal const int MinEmbedMaxTokens = 1;
    internal const int MaxEmbedMaxTokens = 8_192;

    internal const int DefaultEmbedDim = 384;
    internal const int MinEmbedDim = 1;
    internal const int MaxEmbedDim = 65_536;

    /// <summary>
    /// Read an integer env var, applying <paramref name="dflt"/> when it is unset
    /// and rejecting (throwing, naming the var) a value that is non-numeric or
    /// outside <c>[min, max]</c>. The single fail-closed primitive every knob
    /// below routes through.
    /// </summary>
    internal static int ReadInt(string envVar, int dflt, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
            return dflt;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            || v < min || v > max)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in {min}..{max} (default {dflt}).");
        return v;
    }

    internal static int RescanIntervalMin() =>
        ReadInt("INDEXER_RESCAN_INTERVAL_MIN", DefaultRescanIntervalMin, MinRescanIntervalMin, MaxRescanIntervalMin);

    internal static int DebounceSec() =>
        ReadInt("INDEXER_DEBOUNCE_SEC", DefaultDebounceSec, MinDebounceSec, MaxDebounceSec);

    internal static int EmbedIdleSec() =>
        ReadInt("INDEXER_EMBED_IDLE_SEC", DefaultEmbedIdleSec, MinEmbedIdleSec, MaxEmbedIdleSec);

    internal static int EmbedThrottleMs() =>
        ReadInt("INDEXER_EMBED_THROTTLE_MS", DefaultEmbedThrottleMs, MinEmbedThrottleMs, MaxEmbedThrottleMs);

    internal static int HealthPort() =>
        ReadInt("INDEXER_HEALTH_PORT", DefaultHealthPort, MinHealthPort, MaxHealthPort);

    internal static int EmbedMaxTokens() =>
        ReadInt("EMBED_MAX_TOKENS", DefaultEmbedMaxTokens, MinEmbedMaxTokens, MaxEmbedMaxTokens);

    internal static int EmbedDim() =>
        ReadInt("EMBED_DIM", DefaultEmbedDim, MinEmbedDim, MaxEmbedDim);

    // -------------------------------------------------------------------
    // Capability flags (indexer-generalization, docs/architecture/
    // indexer-generalization.md). Every INDEXER_CAP_* + INDEXER_NATS_MODE
    // knob defaults to TODAY'S bundled behaviour when unset, so an image
    // rolled out before a profile/role starts emitting these keys behaves
    // exactly as before: index=on, search.mcp=on, search.http=on,
    // learn_publish=on, nats mode=shared-bus. A disabled capability must
    // wire NOTHING (see Program.cs) — this type only decides on/off.
    // -------------------------------------------------------------------

    internal const bool DefaultCapIndex = true;
    internal const bool DefaultCapSearchMcp = true;
    internal const bool DefaultCapSearchHttp = true;
    internal const bool DefaultCapLearnPublish = true;
    internal const string DefaultNatsMode = "shared-bus";

    /// <summary>Read a boolean capability flag. Unset ⇒ <paramref name="dflt"/>
    /// (back-compat). Truthy: "1"/"true" (case-insensitive). Falsy: "0"/"false".
    /// Any other non-empty value is rejected (fail-closed, names the var) rather
    /// than silently defaulting — a typo must not silently re-enable or
    /// silently disable a capability.</summary>
    internal static bool ReadCap(string envVar, bool dflt)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw)) return dflt;
        if (raw is "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw is "0" || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidOperationException(
            $"{envVar}='{raw}' is invalid: expected true|false|1|0 (default {(dflt ? "true" : "false")}).");
    }

    internal static bool CapIndex() => ReadCap("INDEXER_CAP_INDEX", DefaultCapIndex);
    internal static bool CapSearchMcp() => ReadCap("INDEXER_CAP_SEARCH_MCP", DefaultCapSearchMcp);
    internal static bool CapSearchHttp() => ReadCap("INDEXER_CAP_SEARCH_HTTP", DefaultCapSearchHttp);
    internal static bool CapLearnPublish() => ReadCap("INDEXER_CAP_LEARN_PUBLISH", DefaultCapLearnPublish);

    /// <summary>True when <c>INDEXER_NATS_MODE=isolated</c> (case-insensitive).
    /// Any other non-empty value must be exactly "shared-bus" (fail-closed —
    /// reject an unrecognised mode by name rather than silently treating it as
    /// shared-bus). Unset ⇒ shared-bus (back-compat).</summary>
    internal static bool NatsIsolated()
    {
        var raw = Environment.GetEnvironmentVariable("INDEXER_NATS_MODE");
        if (string.IsNullOrEmpty(raw)) return false; // shared-bus default
        if (string.Equals(raw, "isolated", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "shared-bus", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidOperationException(
            $"INDEXER_NATS_MODE='{raw}' is invalid: expected 'shared-bus' or 'isolated' (default {DefaultNatsMode}).");
    }
}
