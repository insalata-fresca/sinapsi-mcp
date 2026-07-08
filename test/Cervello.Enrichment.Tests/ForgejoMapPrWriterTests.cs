using System.Net;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 unit tests for the LIVE <see cref="ForgejoMapPrWriter"/> (E4's real <c>IMapPrWriter</c>). Two
/// modes: DRY-RUN (the L1 default — assemble + log, NO live forgejo call, NO real map-PR) and the
/// live gitea REST assembly (branch → files → PR) exercised against a MOCK HttpClient. The engine's
/// own R1/R4/R5/R11 self-lint runs UPSTREAM in the CervelloGraphWriter (E4 suite); this proves the
/// writer opens NO real PR at L1 and assembles the right calls when it does. L2: the real
/// ste/cervello PR (dry-run→live flip) with cervello-lint as the pre-merge gate.
/// </summary>
public sealed class ForgejoMapPrWriterTests
{
    private static MapReviewPr Pr() => new(
        branch: "cervello/graph-add-bnd-1",
        title: "cervello graph-add: bnd-1",
        mutations: [new MapMutation("map/people/guilhem.md", "## Timeline",
            "- 2026-07-01 met — source: rec://rec-1#s1", "rec://rec-1#s1", 0.9, "bnd-1", "auto://voice-match@v1")],
        stubs: [new StubDeclaration("acme", "project")],
        bundleRefs: ["bundle://bnd-1"]);

    private static EnrichmentConfig Cfg(bool dryRun) => EnrichmentConfig.From(new Dictionary<string, string?>
    {
        ["CERVELLO_MAP_PR_DRY_RUN"] = dryRun ? "true" : "false",
        ["CERVELLO_FORGEJO_REPO"] = "ste/cervello",
    });

    [Fact]
    public async Task Dry_run_opens_no_real_pr_and_returns_a_null_number_handle()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.OK); // should NEVER be hit
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forgejo.test") };
        var writer = new ForgejoMapPrWriter(http, new StaticBearerProvider("t"), Cfg(dryRun: true));

        var handle = await writer.OpenPrAsync(Pr());

        Assert.Empty(handler.Requests);       // no live forgejo call in dry-run (the L1 boundary)
        Assert.Null(handle.Number);           // null number = dry-run marker
        Assert.Equal("cervello/graph-add-bnd-1", handle.Branch);
    }

    [Fact]
    public async Task Live_mode_creates_branch_writes_stub_then_opens_the_pr()
    {
        // Script: branch (201) → contents (201) → pulls (201, number 42).
        var handler = StubHttpMessageHandler.Custom((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/pulls"))
                return new HttpResponseMessage(HttpStatusCode.Created)
                { Content = new StringContent("""{ "number": 42 }""", System.Text.Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.Created)
            { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forgejo.test") };
        var writer = new ForgejoMapPrWriter(http, new StaticBearerProvider("fp"), Cfg(dryRun: false));

        var handle = await writer.OpenPrAsync(Pr());

        Assert.Equal(42, handle.Number);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("/branches", handler.Requests[0].Uri!.AbsolutePath);
        Assert.Contains("/contents/map/projects/acme.md", handler.Requests[1].Uri!.AbsolutePath);
        Assert.EndsWith("/pulls", handler.Requests[2].Uri!.AbsolutePath);
        Assert.All(handler.Requests, r => Assert.Equal("fp", r.Bearer)); // agent-free bearer on every call
    }

    [Fact]
    public async Task Live_mode_surfaces_a_pr_failure_never_a_silent_success()
    {
        var handler = StubHttpMessageHandler.Custom((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/pulls")
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                : new HttpResponseMessage(HttpStatusCode.Created)
                    { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forgejo.test") };
        var writer = new ForgejoMapPrWriter(http, new StaticBearerProvider("t"), Cfg(dryRun: false));

        await Assert.ThrowsAsync<MapPrWriteException>(() => writer.OpenPrAsync(Pr()));
    }
}
