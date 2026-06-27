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
        HostUrl = (System.Environment.GetEnvironmentVariable("INFISICAL_HOST_URL")
                   ?? "https://infisical.example.com").TrimEnd('/'),
        ClientId = System.Environment.GetEnvironmentVariable("INFISICAL_UNIVERSAL_AUTH_CLIENT_ID") ?? "",
        ClientSecret = System.Environment.GetEnvironmentVariable("INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET") ?? "",
        ProjectId = System.Environment.GetEnvironmentVariable("INFISICAL_PROJECT_ID") ?? "",
        EnvName = System.Environment.GetEnvironmentVariable("INFISICAL_ENV") ?? "dev",
    };
}
