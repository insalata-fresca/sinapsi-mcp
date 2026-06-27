namespace Zitadel.Mcp;

/// <summary>
/// Runtime configuration for the Zitadel.Mcp host. The instance is selected entirely by
/// environment, so a host pointed at any ZITADEL deployment is the same binary configured
/// differently — never forked code:
///   ZITADEL_BASE_URL  the ZITADEL instance root, e.g. https://auth.example.com (the
///                     /management/v1/ and /admin/v1/ API paths are appended per call)
///   ZITADEL_TOKEN     a service-account / PAT bearer token, held server-side, injected at
///                     deploy — never baked in
///   ZITADEL_MCP_PORT  listen port (also overridable via the Sinapsi MapSinapsiMcp default)
///   AGENT_KEY_DIR     host-side directory the create_machine_key tool writes a machine
///                     user's JSON private key into (mode 0640); the key is NEVER returned
///                     to the caller. Defaults to /agent-keys.
/// </summary>
public sealed record ZitadelConfig(string BaseUrl, string Token, int Port, string AgentKeyDir)
{
    public const int DefaultPort = 9220;
    public const string DefaultAgentKeyDir = "/agent-keys";

    public static ZitadelConfig FromEnv()
    {
        var baseUrl = (Env("ZITADEL_BASE_URL") ?? throw new InvalidOperationException(
            "ZITADEL_BASE_URL not set (e.g. https://auth.example.com)."))
            .TrimEnd('/');
        var token = Env("ZITADEL_TOKEN") ?? throw new InvalidOperationException(
            "ZITADEL_TOKEN not set — the ZITADEL service-account/PAT bearer token must be injected at deploy, not baked in.");
        var port = int.TryParse(Env("ZITADEL_MCP_PORT"), out var p) ? p : DefaultPort;
        var agentKeyDir = Env("AGENT_KEY_DIR") ?? DefaultAgentKeyDir;
        return new ZitadelConfig(baseUrl, token, port, agentKeyDir);
    }

    private static string? Env(string k)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
