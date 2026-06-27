using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;
using Zitadel.Mcp.Api;
using Zitadel.Mcp.Tools;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// Pins the server's real registered tool surface: every tool the host advertises
/// (the ones <c>Program.cs</c> registers via <c>WithTools&lt;T&gt;()</c>) is present with
/// the expected MCP name and is marked read-only — the host exposes only reads. One
/// assertion per exposed tool's contract.
/// </summary>
public sealed class ZitadelToolSurfaceTests
{
    // The three tool classes Program.cs registers via WithTools<T>().
    private static readonly Type[] RegisteredToolTypes =
        { typeof(UserTools), typeof(ProjectTools), typeof(OidcAppTools) };

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
    [InlineData(typeof(UserTools), nameof(UserTools.ListUsers), "list_users")]
    [InlineData(typeof(UserTools), nameof(UserTools.GetUser), "get_user")]
    [InlineData(typeof(ProjectTools), nameof(ProjectTools.ListProjects), "list_projects")]
    [InlineData(typeof(OidcAppTools), nameof(OidcAppTools.ListOidcApps), "list_oidc_apps")]
    [InlineData(typeof(OidcAppTools), nameof(OidcAppTools.GetOidcApp), "get_oidc_app")]
    public void Exposed_tool_has_expected_name_and_is_read_only(Type type, string method, string expectedName)
    {
        var (name, readOnly) = Tool(type, method);
        Assert.Equal(expectedName, name);
        Assert.True(readOnly);
    }

    [Fact]
    public void Registered_surface_is_exactly_the_five_expected_read_tools()
    {
        var names = RegisteredToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[] { "get_oidc_app", "get_user", "list_oidc_apps", "list_projects", "list_users" },
            names);
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
}
