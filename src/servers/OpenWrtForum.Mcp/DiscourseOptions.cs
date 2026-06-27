namespace OpenWrtForum.Mcp;

/// <summary>
/// Runtime config, fully env-driven. <see cref="HasCredentials"/> gates the
/// write tools: with no username/password the server runs read-only.
/// </summary>
public sealed record DiscourseOptions(string Url, string Username, string Password)
{
    public bool HasCredentials => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);

    public static DiscourseOptions FromEnvironment() => new(
        Url: (Environment.GetEnvironmentVariable("DISCOURSE_URL") ?? "https://forum.openwrt.org")
            .TrimEnd('/'),
        Username: Environment.GetEnvironmentVariable("DISCOURSE_API_USERNAME") ?? "",
        Password: Environment.GetEnvironmentVariable("DISCOURSE_API_PASSWORD") ?? "");
}
