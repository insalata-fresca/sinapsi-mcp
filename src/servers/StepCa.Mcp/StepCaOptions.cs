namespace StepCa.Mcp;

/// <summary>
/// Env-driven configuration for the step-ca MCP host. Every value is supplied
/// at deploy time; defaults are neutral so the server carries no site- or
/// deployment-specific wiring in source.
/// </summary>
public sealed record StepCaOptions(
    string CaUrl,
    string CaRootCertPath,
    string StepBin,
    string IssuerProvisioner,
    string IssuerPasswordFile,
    int SubprocessTimeoutMs)
{
    /// <summary>Default subprocess timeout (ms) when none is configured.</summary>
    internal const int DefaultSubprocessTimeoutMs = 30_000;

    /// <summary>Upper bound on a configurable subprocess timeout (ms). 10 minutes
    /// is far past any legitimate `step` call; a larger value is treated as a
    /// config error, not honoured.</summary>
    internal const int MaxSubprocessTimeoutMs = 600_000;

    public static StepCaOptions FromEnvironment()
    {
        string Env(string k, string def) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : def;
        string EnvRequired(string k) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
                ? v
                : throw new InvalidOperationException($"{k} is required");

        return new StepCaOptions(
            CaUrl:               EnvRequired("STEP_CA_URL"),
            CaRootCertPath:      Env("STEP_CA_ROOT_CERT",      "/etc/step-ca-mcp/root_ca.crt"),
            StepBin:             Env("STEP_BIN",               "/usr/local/bin/step"),
            IssuerProvisioner:   Env("MCP_ISSUER_PROVISIONER", "mcp-issuer"),
            IssuerPasswordFile:  Env("MCP_ISSUER_PASSWORD_FILE",
                                     "/etc/step-ca-mcp/mcp-issuer-password.txt"),
            SubprocessTimeoutMs: ReadSubprocessTimeoutMs());
    }

    /// <summary>
    /// Bounds the entire <c>step</c> subprocess invocation (not an HTTP call).
    /// Canonical name is <c>STEP_SUBPROCESS_TIMEOUT_MS</c>; <c>STEP_CA_HTTP_TIMEOUT_MS</c>
    /// is a back-compat alias (read only when the canonical var is unset).
    /// <para>
    /// Fail-closed validation: a value of <c>0</c> would make every subprocess
    /// time out instantly, and a negative value throws inside the
    /// <see cref="System.Threading.CancellationTokenSource"/> ctor. Any value
    /// <c>&lt;= 0</c> or above <see cref="MaxSubprocessTimeoutMs"/> is rejected as
    /// invalid config — we throw a clear error naming the offending env var rather
    /// than silently honouring a footgun.
    /// </para>
    /// </summary>
    private static int ReadSubprocessTimeoutMs()
    {
        var (envVar, raw) =
            Environment.GetEnvironmentVariable("STEP_SUBPROCESS_TIMEOUT_MS") is { Length: > 0 } a
                ? ("STEP_SUBPROCESS_TIMEOUT_MS", a)
            : Environment.GetEnvironmentVariable("STEP_CA_HTTP_TIMEOUT_MS") is { Length: > 0 } b
                ? ("STEP_CA_HTTP_TIMEOUT_MS", b)
            : (null, null);

        if (envVar is null || raw is null)
            return DefaultSubprocessTimeoutMs;

        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxSubprocessTimeoutMs)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in 1..{MaxSubprocessTimeoutMs} ms " +
                $"(default {DefaultSubprocessTimeoutMs}).");

        return ms;
    }
}
