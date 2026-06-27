namespace Infisical.Mcp;

/// <summary>
/// Infisical connection + Universal-Auth machine-identity config, read from the
/// environment. The clientId/secret are the MCP's OWN machine identity; inject them
/// at deploy (e.g. via an env file) and never bake them into the image.
/// </summary>
public sealed record InfisicalOptions
{
    public required string HostUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string ProjectId { get; init; }
    public string EnvName { get; init; } = "dev";

    public static InfisicalOptions FromEnvironment() => new()
    {
        // No host is baked into the binary. INFISICAL_HOST_URL is required and is
        // supplied at deploy (e.g. /etc/mcp-gateway/infisical.env); if it is missing
        // we fail fast rather than silently default to ANY instance.
        HostUrl = RequiredHostUrl().TrimEnd('/'),
        ClientId = System.Environment.GetEnvironmentVariable("INFISICAL_UNIVERSAL_AUTH_CLIENT_ID") ?? "",
        ClientSecret = System.Environment.GetEnvironmentVariable("INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET") ?? "",
        ProjectId = System.Environment.GetEnvironmentVariable("INFISICAL_PROJECT_ID") ?? "",
        EnvName = System.Environment.GetEnvironmentVariable("INFISICAL_ENV") ?? "dev",
    };

    private static string RequiredHostUrl()
    {
        var url = System.Environment.GetEnvironmentVariable("INFISICAL_HOST_URL");
        return string.IsNullOrWhiteSpace(url)
            ? throw new InvalidOperationException(
                "INFISICAL_HOST_URL is required (no host is baked into the image); " +
                "supply it via the deploy env (e.g. /etc/mcp-gateway/infisical.env)")
            : url;
    }
}
