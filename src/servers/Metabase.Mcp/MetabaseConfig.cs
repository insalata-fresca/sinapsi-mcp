namespace Metabase.Mcp;

/// <summary>
/// Runtime configuration for the Metabase.Mcp host. The instance is selected entirely by
/// environment, so a host pointed at any Metabase deployment is the same binary configured
/// differently — never forked code:
///   METABASE_BASE_URL  the Metabase root, e.g. https://metrics.example.com (the /api/ paths
///                      are appended per call)
///   METABASE_API_KEY   a Metabase API key, held server-side, sent as the X-API-KEY header,
///                      injected at deploy — never baked in
///   METABASE_MCP_PORT  listen port (also overridable via the Sinapsi MapSinapsiMcp default)
/// </summary>
public sealed record MetabaseConfig(string BaseUrl, string ApiKey, int Port)
{
    public const int DefaultPort = 9221;

    public static MetabaseConfig FromEnv()
    {
        var baseUrl = (Env("METABASE_BASE_URL") ?? throw new InvalidOperationException(
            "METABASE_BASE_URL not set (e.g. https://metrics.example.com)."))
            .TrimEnd('/');
        var apiKey = Env("METABASE_API_KEY") ?? throw new InvalidOperationException(
            "METABASE_API_KEY not set — the Metabase API key must be injected at deploy, not baked in.");
        var port = int.TryParse(Env("METABASE_MCP_PORT"), out var p) ? p : DefaultPort;
        return new MetabaseConfig(baseUrl, apiKey, port);
    }

    private static string? Env(string k)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
