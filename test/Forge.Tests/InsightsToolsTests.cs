using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Sinapsi.Forge;
using Sinapsi.Forge.Gitea;
using Sinapsi.Forge.Tools;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// The <see cref="InsightsTools"/> surface contract:
///   • an EXACT-INVENTORY guard — the class exposes precisely the 14 agreed tool names, no
///     more and no fewer, so a rename or a quiet addition breaks the build rather than
///     silently drifting from the `_catalog.yaml` parent rows that gate identity access;
///   • every tool is ReadOnly (Insights is an analytics READ surface — nothing here writes);
///   • validation short-circuits BEFORE any HTTP call (the transport throws if reached);
///   • on a Gitea-family forge the whole surface answers with the structured NotSupported
///     envelope rather than an unhandled throw.
/// </summary>
public sealed class InsightsToolsTests
{
    /// <summary>The agreed surface. Federated by the gateway as `github_<name>`.</summary>
    private static readonly string[] Expected =
    [
        // traffic
        "get_traffic_views", "get_traffic_clones", "get_traffic_referrers", "get_traffic_paths",
        // activity / contributors
        "list_contributors", "get_contributor_stats", "get_commit_activity",
        "get_code_frequency", "get_participation", "get_punch_card",
        // community / dependencies
        "get_community_profile", "get_sbom", "list_forks",
        // adjacent
        "get_languages",
    ];

    private static IReadOnlyList<(MethodInfo Method, McpServerToolAttribute Attr)> Tools() =>
        typeof(InsightsTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()!))
            .Where(t => t.Attr is not null)
            .ToList();

    /// <summary>A transport that FAILS the test if it is ever reached.</summary>
    private sealed class ThrowIfReachedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new Xunit.Sdk.XunitException($"HTTP must NOT be reached — validation should short-circuit; got {request.Method} {request.RequestUri}");
    }

    private static IForgeClient Gitea(HttpMessageHandler handler)
        => new GiteaForgeClient(new HttpClient(handler) { BaseAddress = new Uri("https://forge.example/api/v1/") });

    private static JsonElement Env(object result) => JsonSerializer.SerializeToElement(result);

    // ── exact inventory ────────────────────────────────────────────────────────

    [Fact]
    public void InsightsTools_exposes_exactly_the_agreed_14_tools()
    {
        var actual = Tools().Select(t => t.Attr.Name!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(Expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
        Assert.Equal(14, actual.Length);
    }

    [Fact]
    public void Every_insights_tool_is_ReadOnly()
    {
        foreach (var (method, attr) in Tools())
            Assert.True(attr.ReadOnly, $"{method.Name} ({attr.Name}) must be ReadOnly — Insights is a read surface.");
    }

    [Fact]
    public void Insights_tool_names_do_not_collide_with_the_rest_of_the_shared_surface()
    {
        // Every other tool class in the library, so a new Insights name can never shadow one.
        var others = typeof(RepoTools).Assembly.GetTypes()
            .Where(t => t != typeof(InsightsTools) && t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in Expected)
            Assert.DoesNotContain(name, others);
    }

    // ── validation short-circuits BEFORE any HTTP call ─────────────────────────

    [Fact]
    public async Task GetTrafficViews_bad_owner_short_circuits_without_touching_http()
    {
        var result = Env(await InsightsTools.GetTrafficViews(Gitea(new ThrowIfReachedHandler()), "-evil", "repo"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("owner", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetTrafficViews_bad_per_short_circuits_without_touching_http()
    {
        var result = Env(await InsightsTools.GetTrafficViews(Gitea(new ThrowIfReachedHandler()), "o", "r", per: "hour"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        var error = result.GetProperty("error").GetString()!;
        Assert.Contains("per", error);
        Assert.Contains("day", error);
        Assert.Contains("week", error);
    }

    [Fact]
    public async Task GetTrafficClones_bad_per_short_circuits_without_touching_http()
    {
        var result = Env(await InsightsTools.GetTrafficClones(Gitea(new ThrowIfReachedHandler()), "o", "r", per: "DAY"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("per", result.GetProperty("error").GetString());   // case-sensitive: "DAY" is not "day"
    }

    [Fact]
    public async Task ListForks_bad_sort_short_circuits_without_touching_http()
    {
        var result = Env(await InsightsTools.ListForks(Gitea(new ThrowIfReachedHandler()), "o", "r", sort: "popular"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("sort", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ListForks_bad_limit_short_circuits_without_touching_http()
    {
        var result = Env(await InsightsTools.ListForks(Gitea(new ThrowIfReachedHandler()), "o", "r", limit: 0));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("limit", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ListContributors_limit_above_the_cap_short_circuits_without_touching_http()
    {
        var result = Env(await InsightsTools.ListContributors(Gitea(new ThrowIfReachedHandler()), "o", "r", limit: 100_000));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("limit", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetSbom_bad_repo_short_circuits_without_touching_http()
    {
        var result = Env(await InsightsTools.GetSbom(Gitea(new ThrowIfReachedHandler()), "o", "bad/repo"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("repo", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetCommunityProfile_empty_owner_short_circuits_without_touching_http()
    {
        var result = Env(await InsightsTools.GetCommunityProfile(Gitea(new ThrowIfReachedHandler()), "  ", "r"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("owner", result.GetProperty("error").GetString());
    }

    // ── Gitea family: the whole surface is NotSupported, as an envelope ─────────

    [Fact]
    public async Task On_a_Gitea_forge_insights_return_a_structured_NotSupported_envelope()
    {
        // Valid params, so validation passes and the adapter is actually called — it throws
        // ForgeNotSupportedException, which the guard must turn into an envelope rather than
        // letting the MCP SDK flatten it to a generic invoke error.
        var forge = Gitea(new ThrowIfReachedHandler());   // no HTTP: the adapter throws before any request

        var result = Env(await InsightsTools.GetLanguages(forge, "o", "r"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("status").ValueKind);
        Assert.Contains("GitHub", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Gitea_capabilities_do_not_advertise_Insights_but_GitHub_style_flag_exists()
    {
        var forge = Gitea(new ThrowIfReachedHandler());
        Assert.False(forge.Capabilities.HasFlag(ForgeCapabilities.Insights));
        Assert.False(ForgeCapabilities.All.HasFlag(ForgeCapabilities.Insights));   // opt-in, like TimeTracking
        await Task.CompletedTask;
    }
}
