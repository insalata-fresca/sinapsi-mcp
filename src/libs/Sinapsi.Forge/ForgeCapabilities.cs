namespace Sinapsi.Forge;

/// <summary>
/// Per-provider capability flags. The common tool classes consult these to hide or
/// short-circuit operations a given forge does not expose (e.g. Gitea time-tracking
/// has no GitHub analogue; GitHub commit-status has no Gitea-MCP analogue here).
/// </summary>
[Flags]
public enum ForgeCapabilities
{
    None              = 0,
    Repos             = 1 << 0,
    Contents          = 1 << 1,
    Branches          = 1 << 2,
    Commits           = 1 << 3,
    Issues            = 1 << 4,
    PullRequests      = 1 << 5,
    Releases          = 1 << 6,
    Orgs              = 1 << 7,
    Notifications     = 1 << 8,
    Search            = 1 << 9,
    Webhooks          = 1 << 10,
    Actions           = 1 << 11,
    TimeTracking      = 1 << 12,   // Gitea-only
    CommitStatus      = 1 << 13,   // GitHub-only (here)
    BranchProtection  = 1 << 14,

    All = Repos | Contents | Branches | Commits | Issues | PullRequests | Releases
        | Orgs | Notifications | Search | Webhooks | Actions | BranchProtection,
}

/// <summary>Thrown when a tool is invoked against a provider that does not support it.</summary>
public sealed class ForgeNotSupportedException(string message) : Exception(message);

/// <summary>A non-2xx forge API response (shared by all adapters).</summary>
public sealed class ForgeApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
