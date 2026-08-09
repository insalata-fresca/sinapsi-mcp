using System.Net;
using System.Text;
using System.Text.Json;
using Github.Mcp.Forge;
using Sinapsi.Forge;
using Xunit;

namespace Github.Mcp.Tests;

/// <summary>
/// The GitHub Insights adapter. The load-bearing legs are the three upstream answers that are
/// NOT failures and must reach the caller as structured envelopes:
///   • <b>202 + EMPTY body</b> on any <c>stats/*</c> endpoint — GitHub warming its cache. This
///     is the one that would otherwise crash: <c>EnsureOkAsync</c> passes 202 (it IS a success
///     status) and <c>JsonDocument.Parse("")</c> then throws;
///   • <b>403</b> on <c>traffic/*</c> — traffic needs push access;
///   • <b>422</b> on <c>stats/code_frequency</c> — repo too large to compute.
/// Plus happy-path JSON mapping for the traffic, stats and plain-list shapes.
/// </summary>
public sealed class GitHubInsightsTests
{
    /// <summary>Returns a fixed status + body for every request, recording the paths asked for.</summary>
    private sealed class StatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        public readonly List<string> Queries = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Queries.Add(request.RequestUri!.Query);
            var resp = new HttpResponseMessage(status);
            // A 202/204 from GitHub carries NO body at all — model that faithfully.
            if (body.Length > 0) resp.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }

    private static (GitHubForgeClient Client, StatusHandler Handler) Client(HttpStatusCode status, string body = "")
    {
        var handler = new StatusHandler(status, body);
        return (new GitHubForgeClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }), handler);
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    // ── THE critical leg: 202 Accepted with an empty body ──────────────────────

    public static TheoryData<string, Func<GitHubForgeClient, Task<object>>> StatsEndpoints() => new()
    {
        { "stats/contributors",   c => c.GetContributorStatsAsync("o", "r") },
        { "stats/commit_activity", c => c.GetCommitActivityAsync("o", "r") },
        { "stats/code_frequency", c => c.GetCodeFrequencyAsync("o", "r") },
        { "stats/participation",  c => c.GetParticipationAsync("o", "r") },
        { "stats/punch_card",     c => c.GetPunchCardAsync("o", "r") },
    };

    [Theory]
    [MemberData(nameof(StatsEndpoints))]
    public async Task Stats_202_with_an_empty_body_returns_a_retry_envelope_and_never_throws(
        string endpoint, Func<GitHubForgeClient, Task<object>> call)
    {
        var (client, handler) = Client(HttpStatusCode.Accepted);   // 202, NO body — the real GitHub shape

        var result = Json(await call(client));                     // must NOT throw

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(202, result.GetProperty("status").GetInt32());
        Assert.True(result.GetProperty("retry").GetBoolean());
        Assert.Contains("computing", result.GetProperty("note").GetString());
        Assert.Contains(endpoint, result.GetProperty("endpoint").GetString());
        Assert.Contains(endpoint, handler.Paths.Single());
    }

    [Fact]
    public async Task Stats_200_with_an_empty_body_is_also_treated_as_not_ready()
    {
        // Belt-and-braces: the same "cache not warm" condition wearing a 200.
        var (client, _) = Client(HttpStatusCode.OK, "   ");
        var result = Json(await client.GetParticipationAsync("o", "r"));
        Assert.Equal(202, result.GetProperty("status").GetInt32());
        Assert.True(result.GetProperty("retry").GetBoolean());
    }

    [Fact]
    public async Task Stats_204_no_content_is_an_empty_result_not_a_retry()
    {
        // A repo with no activity: GitHub answers 204. That is a final answer, not "come back".
        var (client, _) = Client(HttpStatusCode.NoContent);
        var result = Json(await client.GetCommitActivityAsync("o", "r"));
        Assert.Empty(result.GetProperty("weeks").EnumerateArray());
        Assert.False(result.TryGetProperty("retry", out _));
    }

    [Fact]
    public async Task Stats_404_still_throws_on_the_normal_ForgeApiException_path()
    {
        var (client, _) = Client(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var ex = await Assert.ThrowsAsync<ForgeApiException>(() => client.GetContributorStatsAsync("o", "r"));
        Assert.Equal(404, ex.Status);
    }

    // ── 403 on traffic/* ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("views")]
    [InlineData("clones")]
    public async Task Traffic_403_returns_a_forbidden_envelope_not_an_exception(string kind)
    {
        var (client, _) = Client(HttpStatusCode.Forbidden, """{"message":"Must have push access to repository"}""");

        var result = Json(kind == "views"
            ? await client.GetTrafficViewsAsync("o", "r", "day")
            : await client.GetTrafficClonesAsync("o", "r", "day"));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(403, result.GetProperty("status").GetInt32());
        Assert.Contains("push access", result.GetProperty("note").GetString());
    }

    [Fact]
    public async Task TrafficReferrers_and_paths_403_also_return_the_forbidden_envelope()
    {
        var (client, _) = Client(HttpStatusCode.Forbidden, "{}");
        Assert.Equal(403, Json(await client.GetTrafficReferrersAsync("o", "r")).GetProperty("status").GetInt32());
        Assert.Equal(403, Json(await client.GetTrafficPathsAsync("o", "r")).GetProperty("status").GetInt32());
    }

    // ── 422 on code frequency ──────────────────────────────────────────────────

    [Fact]
    public async Task CodeFrequency_422_returns_a_too_large_envelope()
    {
        var (client, _) = Client((HttpStatusCode)422, """{"message":"too many commits"}""");

        var result = Json(await client.GetCodeFrequencyAsync("o", "r"));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(422, result.GetProperty("status").GetInt32());
        Assert.Contains("too many commits", result.GetProperty("note").GetString());
        Assert.False(result.TryGetProperty("retry", out _));      // retrying will never help
    }

    [Fact]
    public async Task A_422_on_another_stats_endpoint_is_NOT_swallowed()
    {
        // The 422 envelope is scoped to code frequency; anywhere else it stays a real error.
        var (client, _) = Client((HttpStatusCode)422, """{"message":"unprocessable"}""");
        var ex = await Assert.ThrowsAsync<ForgeApiException>(() => client.GetPunchCardAsync("o", "r"));
        Assert.Equal(422, ex.Status);
    }

    // ── happy-path JSON mapping ────────────────────────────────────────────────

    [Fact]
    public async Task TrafficViews_maps_totals_and_the_per_day_series_and_passes_per_through()
    {
        const string body = """
        {"count":128,"uniques":40,
         "views":[{"timestamp":"2026-08-01T00:00:00Z","count":100,"uniques":30},
                  {"timestamp":"2026-08-02T00:00:00Z","count":28,"uniques":10}]}
        """;
        var (client, handler) = Client(HttpStatusCode.OK, body);

        var result = Json(await client.GetTrafficViewsAsync("o", "r", "week"));

        Assert.EndsWith("/repos/o/r/traffic/views", handler.Paths.Single());
        Assert.Equal("?per=week", handler.Queries.Single());
        Assert.Equal(128, result.GetProperty("count").GetInt64());
        Assert.Equal(40, result.GetProperty("uniques").GetInt64());
        Assert.Equal("week", result.GetProperty("per").GetString());
        var views = result.GetProperty("views").EnumerateArray().ToList();
        Assert.Equal(2, views.Count);
        Assert.Equal("2026-08-01T00:00:00Z", views[0].GetProperty("timestamp").GetString());
        Assert.Equal(100, views[0].GetProperty("count").GetInt64());
        Assert.Equal(10, views[1].GetProperty("uniques").GetInt64());
    }

    [Fact]
    public async Task CodeFrequency_maps_the_positional_triples_keeping_negative_deletions()
    {
        // GitHub returns [[week_unix, additions, deletions]] with deletions NEGATIVE.
        var (client, _) = Client(HttpStatusCode.OK, "[[1735689600,420,-137],[1736294400,12,0]]");

        var result = Json(await client.GetCodeFrequencyAsync("o", "r"));

        var weeks = result.GetProperty("weeks").EnumerateArray().ToList();
        Assert.Equal(2, weeks.Count);
        Assert.Equal(1735689600, weeks[0].GetProperty("week_start_unix").GetInt64());
        Assert.Equal(420, weeks[0].GetProperty("additions").GetInt64());
        Assert.Equal(-137, weeks[0].GetProperty("deletions").GetInt64());   // verbatim, not abs()
    }

    [Fact]
    public async Task ContributorStats_flattens_the_author_and_the_weekly_a_d_c_series()
    {
        const string body = """
        [{"author":{"login":"ste","id":7},"total":9,
          "weeks":[{"w":1735689600,"a":10,"d":2,"c":3}]}]
        """;
        var (client, _) = Client(HttpStatusCode.OK, body);

        var result = Json(await client.GetContributorStatsAsync("o", "r"));

        var c = result.GetProperty("contributors").EnumerateArray().Single();
        Assert.Equal("ste", c.GetProperty("login").GetString());
        Assert.Equal(7, c.GetProperty("id").GetInt64());
        Assert.Equal(9, c.GetProperty("total").GetInt64());
        var w = c.GetProperty("weeks").EnumerateArray().Single();
        Assert.Equal(1735689600, w.GetProperty("week_start_unix").GetInt64());
        Assert.Equal(10, w.GetProperty("additions").GetInt64());
        Assert.Equal(2, w.GetProperty("deletions").GetInt64());
        Assert.Equal(3, w.GetProperty("commits").GetInt64());
    }

    [Fact]
    public async Task Participation_maps_all_and_owner_series()
    {
        var (client, _) = Client(HttpStatusCode.OK, """{"all":[3,0,5],"owner":[3,0,1]}""");
        var result = Json(await client.GetParticipationAsync("o", "r"));
        Assert.Equal(new[] { 3L, 0L, 5L }, result.GetProperty("all").EnumerateArray().Select(x => x.GetInt64()));
        Assert.Equal(new[] { 3L, 0L, 1L }, result.GetProperty("owner_only").EnumerateArray().Select(x => x.GetInt64()));
    }

    [Fact]
    public async Task Languages_maps_bytes_per_language_and_totals_them()
    {
        var (client, handler) = Client(HttpStatusCode.OK, """{"C#":90000,"Shell":1000}""");

        var result = Json(await client.GetLanguagesAsync("o", "r"));

        Assert.EndsWith("/repos/o/r/languages", handler.Paths.Single());
        Assert.Equal(90000, result.GetProperty("languages").GetProperty("C#").GetInt64());
        Assert.Equal(91000, result.GetProperty("total_bytes").GetInt64());
    }

    [Fact]
    public async Task ListContributors_maps_named_and_anonymous_entries_and_sends_anon_flag()
    {
        const string body = """
        [{"login":"ste","id":7,"type":"User","contributions":42,"html_url":"https://github.com/ste"},
         {"name":"Old Committer","email":"old@example.com","type":"Anonymous","contributions":3}]
        """;
        var (client, handler) = Client(HttpStatusCode.OK, body);

        var result = Json(await client.ListContributorsAsync("o", "r", anon: true, limit: 50));

        Assert.Contains("anon=1", handler.Queries.Single());
        Assert.Contains("per_page=50", handler.Queries.Single());
        var list = result.GetProperty("contributors").EnumerateArray().ToList();
        Assert.Equal("ste", list[0].GetProperty("login").GetString());
        Assert.Equal(42, list[0].GetProperty("contributions").GetInt64());
        Assert.Equal(JsonValueKind.Null, list[1].GetProperty("login").ValueKind);   // anonymous: no login
        Assert.Equal("old@example.com", list[1].GetProperty("email").GetString());
    }

    [Fact]
    public async Task Sbom_reports_the_full_package_count_and_truncates_to_the_limit()
    {
        const string body = """
        {"sbom":{"SPDXID":"SPDXRef-DOCUMENT","spdxVersion":"SPDX-2.3","name":"com.github.o/r",
          "dataLicense":"CC0-1.0","documentNamespace":"https://github.com/o/r/dependency_graph/sbom-1",
          "packages":[
            {"name":"xunit","versionInfo":"2.9.2","licenseConcluded":"Apache-2.0",
             "externalRefs":[{"referenceType":"purl","referenceLocator":"pkg:nuget/xunit@2.9.2"}]},
            {"name":"Serilog","versionInfo":"4.0.0","licenseDeclared":"Apache-2.0"},
            {"name":"Npgsql","versionInfo":"8.0.3"}]}}
        """;
        var (client, handler) = Client(HttpStatusCode.OK, body);

        var result = Json(await client.GetSbomAsync("o", "r", limit: 2));

        Assert.EndsWith("/repos/o/r/dependency-graph/sbom", handler.Paths.Single());
        Assert.Equal("SPDX-2.3", result.GetProperty("spdx_version").GetString());
        Assert.Equal(3, result.GetProperty("package_count").GetInt32());   // FULL count, not the page
        Assert.True(result.GetProperty("truncated").GetBoolean());
        var pkgs = result.GetProperty("packages").EnumerateArray().ToList();
        Assert.Equal(2, pkgs.Count);
        Assert.Equal("2.9.2", pkgs[0].GetProperty("version").GetString());
        Assert.Equal("pkg:nuget/xunit@2.9.2", pkgs[0].GetProperty("purl").GetString());
        Assert.Equal("Apache-2.0", pkgs[1].GetProperty("license").GetString());   // falls back to licenseDeclared
    }

    [Fact]
    public async Task CommunityProfile_maps_health_and_the_present_absent_file_slots()
    {
        const string body = """
        {"health_percentage":75,"description":"a repo","documentation":null,
         "updated_at":"2026-08-01T00:00:00Z","content_reports_count":0,
         "files":{"readme":{"html_url":"https://github.com/o/r/blob/main/README.md"},
                  "license":{"html_url":"https://github.com/o/r/blob/main/LICENSE"},
                  "contributing":null,"code_of_conduct":null,
                  "issue_template":null,"pull_request_template":null}}
        """;
        var (client, _) = Client(HttpStatusCode.OK, body);

        var result = Json(await client.GetCommunityProfileAsync("o", "r"));

        Assert.Equal(75, result.GetProperty("health_percentage").GetInt64());
        var files = result.GetProperty("files");
        Assert.EndsWith("README.md", files.GetProperty("readme").GetString());
        Assert.Equal(JsonValueKind.Null, files.GetProperty("contributing").ValueKind);
    }

    [Fact]
    public async Task ListForks_passes_sort_and_per_page_and_maps_the_fork_rows()
    {
        const string body = """
        [{"full_name":"someone/r","owner":{"login":"someone"},"html_url":"https://github.com/someone/r",
          "description":"a fork","private":false,"stargazers_count":4,"forks_count":1,
          "created_at":"2026-01-01T00:00:00Z","updated_at":"2026-02-01T00:00:00Z"}]
        """;
        var (client, handler) = Client(HttpStatusCode.OK, body);

        var result = Json(await client.ListForksAsync("o", "r", "stargazers", 10));

        Assert.Contains("sort=stargazers", handler.Queries.Single());
        Assert.Contains("per_page=10", handler.Queries.Single());
        var fork = result.GetProperty("forks").EnumerateArray().Single();
        Assert.Equal("someone/r", fork.GetProperty("full_name").GetString());
        Assert.Equal("someone", fork.GetProperty("owner").GetString());
        Assert.Equal(4, fork.GetProperty("stars").GetInt64());
    }

    [Fact]
    public async Task TrafficPaths_and_referrers_map_their_top_ten_rows()
    {
        var (paths, _) = Client(HttpStatusCode.OK, """[{"path":"/o/r","title":"o/r: thing","count":50,"uniques":12}]""");
        var p = Json(await paths.GetTrafficPathsAsync("o", "r")).GetProperty("paths").EnumerateArray().Single();
        Assert.Equal("/o/r", p.GetProperty("path").GetString());
        Assert.Equal(12, p.GetProperty("uniques").GetInt64());

        var (refs, _) = Client(HttpStatusCode.OK, """[{"referrer":"Google","count":9,"uniques":4}]""");
        var r = Json(await refs.GetTrafficReferrersAsync("o", "r")).GetProperty("referrers").EnumerateArray().Single();
        Assert.Equal("Google", r.GetProperty("referrer").GetString());
        Assert.Equal(9, r.GetProperty("count").GetInt64());
    }

    [Fact]
    public void GitHub_capabilities_advertise_Insights()
    {
        var (client, _) = Client(HttpStatusCode.OK, "{}");
        Assert.True(client.Capabilities.HasFlag(ForgeCapabilities.Insights));
    }
}
