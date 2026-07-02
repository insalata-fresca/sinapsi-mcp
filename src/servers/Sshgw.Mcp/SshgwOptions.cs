namespace Sshgw.Mcp;

/// <summary>
/// Env-driven config. The server registry itself (hosts + per-server command
/// whitelist + per-server read_file policy) lives in a JSON file at
/// <see cref="ServerRegistryPath"/>, supplied at deploy time (mount it read-only;
/// keep the SSH identity key out of the image).
///
/// <para>
/// Every numeric option is <b>fail-closed</b>: it has a neutral default AND a hard
/// ceiling. A non-integer, <c>&lt;= 0</c>, or above-ceiling value is rejected with
/// a clear error naming the offending env var, rather than being silently swapped
/// for the default (the old behaviour) — a config typo that would have produced a
/// dangerous bound (a zero timeout that fails every call, an unbounded byte cap)
/// now stops startup instead of running with a footgun. The static
/// <c>Default*</c> / <c>Max*</c> constants are asserted at type-load time to keep
/// every default within its own ceiling.
/// </para>
/// </summary>
public sealed record SshgwOptions(
    string ServerRegistryPath,
    int ConnectTimeoutMs,
    int CommandTimeoutMs,
    int ReadFileDefaultMaxBytes,
    int ReadFileHardMaxBytes,
    bool RequireHostKeyPin = false)
{
    // ── SSH connect timeout ──────────────────────────────────────────────────
    /// <summary>Default SSH connect timeout (ms).</summary>
    internal const int DefaultConnectTimeoutMs = 10_000;
    /// <summary>Hard ceiling on the SSH connect timeout (ms). 2 minutes is far past
    /// any reachable host; a larger value is a config error, not honoured.</summary>
    internal const int MaxConnectTimeoutMs = 120_000;

    // ── per-command timeout ──────────────────────────────────────────────────
    /// <summary>Default per-command timeout (ms).</summary>
    internal const int DefaultCommandTimeoutMs = 30_000;
    /// <summary>Hard ceiling on the per-command timeout (ms). 10 minutes is far past
    /// any legitimate read-only diagnostic command.</summary>
    internal const int MaxCommandTimeoutMs = 600_000;

    // ── read_file default cap ────────────────────────────────────────────────
    /// <summary>Default read_file byte cap (256 KiB).</summary>
    internal const int DefaultReadFileDefaultMaxBytes = 262_144;
    /// <summary>Hard ceiling on the read_file <em>default</em> byte cap. It must not
    /// exceed the hard byte ceiling below (asserted at type load).</summary>
    internal const int MaxReadFileDefaultMaxBytes = 16_777_216; // 16 MiB

    // ── read_file hard cap ───────────────────────────────────────────────────
    /// <summary>Default read_file hard byte ceiling (2 MiB).</summary>
    internal const int DefaultReadFileHardMaxBytes = 2_097_152;
    /// <summary>Absolute ceiling on the configurable read_file hard cap (64 MiB).
    /// Bounds the largest single response a caller can ever pull back.</summary>
    internal const int MaxReadFileHardMaxBytes = 67_108_864; // 64 MiB

    static SshgwOptions()
    {
        // A default that exceeds its own hard ceiling is a build-time mistake, not a
        // runtime one — surface it loudly at type load.
        AssertDefaultWithinCeiling("DefaultConnectTimeoutMs", DefaultConnectTimeoutMs, "MaxConnectTimeoutMs", MaxConnectTimeoutMs);
        AssertDefaultWithinCeiling("DefaultCommandTimeoutMs", DefaultCommandTimeoutMs, "MaxCommandTimeoutMs", MaxCommandTimeoutMs);
        AssertDefaultWithinCeiling("DefaultReadFileDefaultMaxBytes", DefaultReadFileDefaultMaxBytes, "MaxReadFileDefaultMaxBytes", MaxReadFileDefaultMaxBytes);
        AssertDefaultWithinCeiling("DefaultReadFileHardMaxBytes", DefaultReadFileHardMaxBytes, "MaxReadFileHardMaxBytes", MaxReadFileHardMaxBytes);
    }

    public static SshgwOptions FromEnvironment()
    {
        string Env(string k, string def) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : def;

        var defaultCap = ReadBoundedInt("SSHGW_READFILE_DEFAULT_MAX_BYTES", DefaultReadFileDefaultMaxBytes, MaxReadFileDefaultMaxBytes);
        var hardCap = ReadBoundedInt("SSHGW_READFILE_HARD_MAX_BYTES", DefaultReadFileHardMaxBytes, MaxReadFileHardMaxBytes);

        // Cross-field fail-closed rule: the per-call default cap must never exceed
        // the hard ceiling, or a caller-omitted max_bytes would ask for more than
        // the ceiling allows. Reject the inconsistent pair naming both vars.
        if (defaultCap > hardCap)
            throw new InvalidOperationException(
                $"SSHGW_READFILE_DEFAULT_MAX_BYTES ({defaultCap}) must not exceed " +
                $"SSHGW_READFILE_HARD_MAX_BYTES ({hardCap}).");

        return new SshgwOptions(
            ServerRegistryPath:      Env("SSHGW_CONFIG_FILE", "/etc/sshgw/servers.json"),
            ConnectTimeoutMs:        ReadBoundedInt("SSHGW_CONNECT_TIMEOUT_MS", DefaultConnectTimeoutMs, MaxConnectTimeoutMs),
            CommandTimeoutMs:        ReadBoundedInt("SSHGW_COMMAND_TIMEOUT_MS", DefaultCommandTimeoutMs, MaxCommandTimeoutMs),
            ReadFileDefaultMaxBytes: defaultCap,
            ReadFileHardMaxBytes:    hardCap,
            // Global host-key posture: when set, even a server WITHOUT a configured
            // fingerprint is refused (no trust-on-first-use anywhere). Off by default
            // so per-server pins are opt-in; flip on once every server is pinned.
            RequireHostKeyPin:       ReadBool("SSHGW_REQUIRE_HOST_KEY_PIN", false));
    }

    /// <summary>Read a boolean env var. Unset ⇒ <paramref name="def"/>. Accepts
    /// <c>1/0</c>, <c>true/false</c>, <c>yes/no</c>, <c>on/off</c> (case-insensitive);
    /// any other value THROWS naming the offending var rather than guessing.</summary>
    private static bool ReadBool(string envVar, bool def)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (raw is not { Length: > 0 }) return def;
        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected a boolean (1/0, true/false, yes/no, on/off)."),
        };
    }

    /// <summary>
    /// Read an integer env var that must be a positive value within
    /// <c>1..max</c>. Unset ⇒ <paramref name="def"/>. A non-integer, <c>&lt;= 0</c>,
    /// or above-ceiling value THROWS an <see cref="InvalidOperationException"/>
    /// naming the offending var, rather than silently falling back to the default
    /// (which would mask a config typo behind a plausible-looking bound).
    /// </summary>
    private static int ReadBoundedInt(string envVar, int def, int max)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (raw is not { Length: > 0 })
            return def;

        if (!int.TryParse(raw, out var value) || value <= 0 || value > max)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in 1..{max} (default {def}).");

        return value;
    }

    private static void AssertDefaultWithinCeiling(string defName, int def, string maxName, int max)
    {
        if (def <= 0 || def > max)
            throw new InvalidOperationException(
                $"{defName} ({def}) must be within 1..{maxName} ({max}) — fix the source constant.");
    }
}
