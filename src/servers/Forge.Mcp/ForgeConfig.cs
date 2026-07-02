namespace Forge.Mcp;

/// <summary>
/// Runtime configuration for the Forge.Mcp host. One image serves any Gitea-API forge
/// (e.g. Forgejo or Codeberg); the instance is selected entirely by environment, so the
/// forgejo and codeberg deployments are the same binary configured differently — never
/// forked code:
///   FORGE_NAME      logical backend name — e.g. "forgejo" | "codeberg" (server-info + diagnostics)
///   FORGE_BASE_URL  the forge root, e.g. https://forge.example.com  (the /api/v1/ suffix is added)
///   FORGE_TOKEN     a Gitea/Forgejo personal access token, held server-side, injected at deploy
///   FORGE_TOOLSETS  optional comma list to opt out of optional tool groups on a forge that lacks
///                   them — e.g. "-topics,-actions" for a Codeberg instance without those endpoints
///   FORGE_MCP_PORT  listen port (also overridable via the Sinapsi MapSinapsiMcp default)
/// </summary>
public sealed record ForgeConfig(string Name, string ApiBaseUrl, string Token, int Port, ForgeToolsets Toolsets)
{
    public static ForgeConfig FromEnv()
    {
        var name = Env("FORGE_NAME") ?? "forgejo";
        var baseUrl = (Env("FORGE_BASE_URL") ?? throw new InvalidOperationException(
            "FORGE_BASE_URL not set (e.g. https://forge.example.com)."))
            .TrimEnd('/');
        var token = Env("FORGE_TOKEN") ?? throw new InvalidOperationException(
            "FORGE_TOKEN not set — the Gitea/Forgejo personal access token must be injected at deploy, not baked in.");
        var port = int.TryParse(Env("FORGE_MCP_PORT"), out var p) ? p : 9219;
        var toolsets = ForgeToolsets.Parse(Env("FORGE_TOOLSETS"));
        return new ForgeConfig(name, $"{baseUrl}/api/v1/", token, port, toolsets);
    }

    private static string? Env(string k)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}

/// <summary>
/// Which optional tool groups the configured forge exposes. The full Gitea surface is the
/// default; a forge that lacks a given group (some Codeberg instances expose neither the
/// repository-topics nor the Actions endpoints) opts out via FORGE_TOOLSETS, e.g.
/// <c>FORGE_TOOLSETS=-topics,-actions</c>. This keeps a single configurable binary instead
/// of a per-forge fork of the tool registration.
/// </summary>
public sealed record ForgeToolsets(bool Topics, bool Actions)
{
    public static ForgeToolsets Parse(string? spec)
    {
        var topics = true;
        var actions = true;
        if (!string.IsNullOrWhiteSpace(spec))
        {
            foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var token = raw;
                var enable = !token.StartsWith('-');
                token = token.TrimStart('+', '-').ToLowerInvariant();
                switch (token)
                {
                    case "topics": topics = enable; break;
                    case "actions": actions = enable; break;
                }
            }
        }
        return new ForgeToolsets(topics, actions);
    }
}
