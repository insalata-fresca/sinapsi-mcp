using System.ComponentModel;
using ModelContextProtocol.Server;
using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Tools;

/// <summary>ZITADEL project tools (read + create). The host injects the <see cref="ZitadelClient"/>.</summary>
[McpServerToolType]
public sealed class ProjectTools
{
    [McpServerTool(Name = "list_projects", ReadOnly = true)]
    [Description("List projects in the ZITADEL instance (first page).")]
    public static Task<object> ListProjects(
        ZitadelClient zitadel,
        [Description("Max results (default 100).")] int limit = 100,
        CancellationToken ct = default)
        => ZitadelToolGuard.RunAsync(async () => await zitadel.ListProjectsAsync(limit, ct));

    [McpServerTool(Name = "create_project", Destructive = false)]
    [Description("Create a new project. Returns {id, details}. MUTATES.")]
    public static Task<object> CreateProject(
        ZitadelClient zitadel,
        [Description("Project name.")] string name,
        CancellationToken ct = default)
        => ZitadelToolGuard.RunAsync(async () => await zitadel.CreateProjectAsync(name, ct));
}
