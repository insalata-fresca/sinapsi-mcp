namespace StepCa.Mcp;

/// <summary>
/// Env-driven configuration for the step-ca MCP host. Every value is supplied
/// at deploy time; defaults are neutral so the server carries no site- or
/// deployment-specific wiring in source.
/// </summary>
public sealed record StepCaOptions(
    string CaUrl,
    string CaRootCertPath,
    string CaFingerprint,
    string StepBin,
    string IssuerProvisioner,
    string IssuerPasswordFile,
    int SubprocessTimeoutMs)
{
    public static StepCaOptions FromEnvironment()
    {
        string Env(string k, string def) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : def;
        string EnvRequired(string k) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
                ? v
                : throw new InvalidOperationException($"{k} is required");

        // Bounds the entire `step` subprocess invocation (not an HTTP call).
        // Canonical name is STEP_SUBPROCESS_TIMEOUT_MS; STEP_CA_HTTP_TIMEOUT_MS
        // is a back-compat alias (read only when the canonical var is unset).
        int subprocessTimeoutMs =
            int.TryParse(Environment.GetEnvironmentVariable("STEP_SUBPROCESS_TIMEOUT_MS"), out var newV) ? newV
            : int.TryParse(Environment.GetEnvironmentVariable("STEP_CA_HTTP_TIMEOUT_MS"), out var oldV) ? oldV
            : 30_000;

        return new StepCaOptions(
            CaUrl:               EnvRequired("STEP_CA_URL"),
            CaRootCertPath:      Env("STEP_CA_ROOT_CERT",      "/etc/step-ca-mcp/root_ca.crt"),
            CaFingerprint:       Env("STEP_CA_FINGERPRINT",    ""),
            StepBin:             Env("STEP_BIN",               "/usr/local/bin/step"),
            IssuerProvisioner:   Env("MCP_ISSUER_PROVISIONER", "mcp-issuer"),
            IssuerPasswordFile:  Env("MCP_ISSUER_PASSWORD_FILE",
                                     "/etc/step-ca-mcp/mcp-issuer-password.txt"),
            SubprocessTimeoutMs: subprocessTimeoutMs);
    }
}
