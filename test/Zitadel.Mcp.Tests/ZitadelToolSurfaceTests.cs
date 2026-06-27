using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;
using Zitadel.Mcp.Api;
using Zitadel.Mcp.Tools;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// Pins the server's real registered tool surface: every tool the host advertises
/// (the ones <c>Program.cs</c> registers via <c>WithTools&lt;T&gt;()</c>) is present with
/// the expected MCP name and its read/mutate classification. The surface is the full
/// 15-tool ZITADEL management surface — the read tools plus the mutating CRUD + the M2M
/// machine-identity lifecycle (create/update/delete machine user, PAT + JSON key issuance)
/// the homelab secret-delivery canon depends on.
/// </summary>
public sealed class ZitadelToolSurfaceTests
{
    // The four tool classes Program.cs registers via WithTools<T>().
    private static readonly Type[] RegisteredToolTypes =
        { typeof(UserTools), typeof(ProjectTools), typeof(OidcAppTools), typeof(MachineUserTools) };

    private static (string Name, bool ReadOnly) Tool(Type type, string method)
    {
        var attr = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .GetCustomAttribute<McpServerToolAttribute>()!;
        return (attr.Name!, attr.ReadOnly);
    }

    [Fact]
    public void Every_registered_tool_class_is_an_McpServerToolType()
    {
        foreach (var t in RegisteredToolTypes)
            Assert.NotNull(t.GetCustomAttribute<McpServerToolTypeAttribute>());
    }

    [Theory]
    // reads
    [InlineData(typeof(UserTools), nameof(UserTools.ListUsers), "list_users", true)]
    [InlineData(typeof(UserTools), nameof(UserTools.GetUser), "get_user", true)]
    [InlineData(typeof(ProjectTools), nameof(ProjectTools.ListProjects), "list_projects", true)]
    [InlineData(typeof(OidcAppTools), nameof(OidcAppTools.ListOidcApps), "list_oidc_apps", true)]
    [InlineData(typeof(OidcAppTools), nameof(OidcAppTools.GetOidcApp), "get_oidc_app", true)]
    // mutations
    [InlineData(typeof(ProjectTools), nameof(ProjectTools.CreateProject), "create_project", false)]
    [InlineData(typeof(OidcAppTools), nameof(OidcAppTools.CreateOidcApp), "create_oidc_app", false)]
    [InlineData(typeof(OidcAppTools), nameof(OidcAppTools.UpdateOidcAppConfig), "update_oidc_app_config", false)]
    [InlineData(typeof(OidcAppTools), nameof(OidcAppTools.DeleteOidcApp), "delete_oidc_app", false)]
    [InlineData(typeof(OidcAppTools), nameof(OidcAppTools.RegenerateOidcSecret), "regenerate_oidc_secret", false)]
    [InlineData(typeof(MachineUserTools), nameof(MachineUserTools.CreateMachineUser), "create_machine_user", false)]
    [InlineData(typeof(MachineUserTools), nameof(MachineUserTools.UpdateMachineUser), "update_machine_user", false)]
    [InlineData(typeof(MachineUserTools), nameof(MachineUserTools.DeleteMachineUser), "delete_machine_user", false)]
    [InlineData(typeof(MachineUserTools), nameof(MachineUserTools.CreatePat), "create_pat", false)]
    [InlineData(typeof(MachineUserTools), nameof(MachineUserTools.CreateMachineKey), "create_machine_key", false)]
    public void Exposed_tool_has_expected_name_and_classification(Type type, string method, string expectedName, bool readOnly)
    {
        var (name, isReadOnly) = Tool(type, method);
        Assert.Equal(expectedName, name);
        Assert.Equal(readOnly, isReadOnly);
    }

    [Fact]
    public void Registered_surface_is_exactly_the_fifteen_expected_tools()
    {
        var names = RegisteredToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "create_machine_key", "create_machine_user", "create_oidc_app", "create_pat", "create_project",
                "delete_machine_user", "delete_oidc_app", "get_oidc_app", "get_user",
                "list_oidc_apps", "list_projects", "list_users",
                "regenerate_oidc_secret", "update_machine_user", "update_oidc_app_config",
            },
            names);
    }

    [Fact]
    public void The_m2m_identity_minting_tools_are_present()
    {
        // The homelab secret-delivery canon depends on minting a machine identity + its key/PAT
        // from this MCP — these must never be silently dropped from the surface.
        var names = RegisteredToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .ToHashSet();

        Assert.Contains("create_machine_user", names);
        Assert.Contains("create_machine_key", names);
        Assert.Contains("create_pat", names);
    }

    [Fact]
    public async Task Tool_guard_maps_an_api_failure_to_a_structured_error_payload()
    {
        // Every tool body runs through ZitadelToolGuard; a real upstream failure becomes a
        // legible { ok:false, status, error } rather than the SDK's generic invoke message.
        var result = await ZitadelToolGuard.RunAsync(
            () => throw new ZitadelApiException(404, "404 Not Found: no such project"));

        var ok = (bool)result.GetType().GetProperty("ok")!.GetValue(result)!;
        var status = (int)result.GetType().GetProperty("status")!.GetValue(result)!;
        Assert.False(ok);
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Tool_guard_returns_a_successful_payload_unchanged()
    {
        var payload = new { result = "ok" };
        var result = await ZitadelToolGuard.RunAsync(() => Task.FromResult<object>(payload));
        Assert.Same(payload, result);
    }

    [Theory]
    [InlineData("agent-journey-ux", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    public void CreateMachineKey_basename_guard_blocks_path_traversal(string name, bool expected)
        => Assert.Equal(expected, MachineUserTools.IsSafeBasename(name));
}
