namespace Sshgw.Mcp;

/// <summary>
/// Env-driven config. The server registry itself (hosts + per-server command
/// whitelist + per-server read_file policy) lives in a JSON file at
/// <see cref="ServerRegistryPath"/>, supplied at deploy time (mount it read-only;
/// keep the SSH identity key out of the image).
/// </summary>
public sealed record SshgwOptions(
    string ServerRegistryPath,
    int ConnectTimeoutMs,
    int CommandTimeoutMs,
    int ReadFileDefaultMaxBytes,
    int ReadFileHardMaxBytes)
{
    public static SshgwOptions FromEnvironment()
    {
        int EnvInt(string k, int def) =>
            int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : def;
        string Env(string k, string def) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : def;

        return new SshgwOptions(
            ServerRegistryPath:      Env("SSHGW_CONFIG_FILE", "/etc/sshgw/servers.json"),
            ConnectTimeoutMs:        EnvInt("SSHGW_CONNECT_TIMEOUT_MS", 10_000),
            CommandTimeoutMs:        EnvInt("SSHGW_COMMAND_TIMEOUT_MS", 30_000),
            // read_file context guards: default 256 KiB, hard ceiling 2 MiB.
            // A caller may request up to the hard cap.
            ReadFileDefaultMaxBytes: EnvInt("SSHGW_READFILE_DEFAULT_MAX_BYTES", 262_144),
            ReadFileHardMaxBytes:    EnvInt("SSHGW_READFILE_HARD_MAX_BYTES", 2_097_152));
    }
}
