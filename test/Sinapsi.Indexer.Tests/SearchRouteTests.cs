// ---------------------------------------------------------------------------
// SearchRouteTests - hermetic integration tests for GET /search.
//
// Strategy: build a MINIMAL WebApplication with ONLY the /search route and its
// dependencies (IIndexStore fake, JsonOptions). No NATS, no Postgres, no ONNX.
// This lets us test the full HTTP pipeline — routing, parameter binding,
// validation, serialisation — without the background worker complexity.
//
// The test covers:
//   1. 400 on missing / empty / invalid query parameters.
//   2. 200 with correct JSON shape when the store returns hits.
//   3. 200 with empty result set.
//   4. Source and limit forwarding to the store.
//   5. 500 with redacted error when the store throws with a secret in the message.
//   6. Seam contract: the route trusts the store to never return tombstoned or
//      secret-path rows (documented via a controlled fake).
//   7. M5-secure: the Bearer-token gate — missing/wrong token => 401 BEFORE the
//      store is ever touched; correct token => 200. This is the missing
//      enforcement the mission fixed (INDEXER_SEARCH_TOKEN was previously read
//      nowhere in the service).
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

// ---------------------------------------------------------------------------
// Controllable fake store
// ---------------------------------------------------------------------------

/// <summary>
/// A controllable <see cref="IIndexStore"/> for HTTP route tests: the
/// <c>SearchAsync</c> implementation is supplied by the caller via a delegate
/// so each test can inject exactly the results (or exception) it needs.
/// </summary>
internal sealed class ControllableIndexStore : IIndexStore
{
    private readonly Func<string, string?, string?, int, CancellationToken, Task<IReadOnlyList<SearchHit>>> _searchImpl;

    public ControllableIndexStore(
        Func<string, string?, string?, int, CancellationToken, Task<IReadOnlyList<SearchHit>>> searchImpl)
    {
        _searchImpl = searchImpl;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string q, string? source, string? kind, int limit, CancellationToken ct)
        => _searchImpl(q, source, kind, limit, ct);

    // Unused by the route under test — minimal stubs.
    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> UpsertAsync(Document doc, CancellationToken ct) => Task.FromResult(false);
    public Task<int> TombstoneMissingAsync(string source, IReadOnlyCollection<string> presentDocIds, CancellationToken ct) => Task.FromResult(0);
    public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<LearningHit>> GetLearningsAsync(string? scope, string? query, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<LearningHit>>(Array.Empty<LearningHit>());
    public Task SetEmbeddingAsync(string docId, float[] vector, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<(string DocId, string Title, string Body)>> GetMissingEmbeddingsAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<(string, string, string)>>(Array.Empty<(string, string, string)>());
    public Task<IReadOnlyList<SearchHit>> SemanticSearchAsync(float[] queryVector, string queryText, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SearchHit>>(Array.Empty<SearchHit>());
}

// ---------------------------------------------------------------------------
// Minimal route host
// ---------------------------------------------------------------------------

/// <summary>
/// Builds and starts a MINIMAL ASP.NET Core <see cref="TestServer"/> that wires
/// ONLY the <c>GET /search</c> route, backed by the supplied fake store.
/// No NATS worker, no Postgres, no ONNX — hermetic. Disposable.
/// </summary>
internal static class SearchRouteHost
{
    /// <summary>Build a TestServer hosting only GET /search with the given store.
    /// When <paramref name="searchToken"/> is non-null, the route enforces the
    /// Bearer-token gate (mirrors Program.cs exactly, including the M5-secure
    /// fix); when null, callers get the pre-fix / "no capability" behaviour is
    /// NOT exercised by this host — every test below passes a token so the gate
    /// is always live, matching the fixed production route.</summary>
    public static TestServer Build(IIndexStore store, string? searchToken = "test-search-token")
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton(store);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    // Mirror the GET /search route from Program.cs exactly,
                    // including the M5-secure Bearer-token gate.
                    endpoints.MapGet("/search", async (
                        HttpContext ctx,
                        IIndexStore s,
                        string? q,
                        string? limit,
                        string? source,
                        CancellationToken cancellationToken) =>
                    {
                        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
                        if (!SearchAuth.IsAuthorized(authHeader, searchToken))
                        {
                            await SearchAuth.WriteUnauthorizedAsync(ctx);
                            return;
                        }

                        var (req, err) = SearchRequest.TryParse(q, limit, source);
                        if (err is not null)
                        {
                            await Results.Json(new { error = err }, statusCode: 400).ExecuteAsync(ctx);
                            return;
                        }

                        try
                        {
                            var hits = await s.SearchAsync(req!.Query, req.Source, kind: null, req.Limit, cancellationToken);
                            var items = hits
                                .Select(h => new SearchResultItem(h.Source, h.Path, h.Kind, h.Title, h.Scope, h.Snippet, h.Score))
                                .ToList();
                            await Results.Json(new SearchResponse(req.Query, items.Count, items)).ExecuteAsync(ctx);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e)
                        {
                            await Results.Json(new { error = IndexerErrors.FromException(e) }, statusCode: 500).ExecuteAsync(ctx);
                        }
                    });
                });
            });

        return new TestServer(builder);
    }

    /// <summary>Convenience: attach "Authorization: Bearer &lt;token&gt;" to a request.</summary>
    public static void SetBearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
}

// ---------------------------------------------------------------------------
// Route tests
// ---------------------------------------------------------------------------

public sealed class SearchRouteTests
{
    // ── 400 on invalid input ─────────────────────────────────────────────────

    [Theory]
    [InlineData("")]          // empty q
    [InlineData("   ")]       // whitespace-only q
    public async Task Search_EmptyQuery_Returns400(string q)
    {
        using var server = SearchRouteHost.Build(new ThrowingIndexStore());
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync($"/search?q={Uri.EscapeDataString(q)}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.False(string.IsNullOrWhiteSpace(err.GetString()));
    }

    [Fact]
    public async Task Search_MissingQ_Returns400()
    {
        using var server = SearchRouteHost.Build(new ThrowingIndexStore());
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Search_BadLimit_Returns400()
    {
        using var server = SearchRouteHost.Build(new ThrowingIndexStore());
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search?q=test&limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Search_AbsurdLimit_Returns400()
    {
        using var server = SearchRouteHost.Build(new ThrowingIndexStore());
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search?q=test&limit=999");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Search_NonNumericLimit_Returns400()
    {
        using var server = SearchRouteHost.Build(new ThrowingIndexStore());
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search?q=test&limit=abc");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── 200 with hits ────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ValidQuery_Returns200_WithRankedHits()
    {
        var expectedHits = new List<SearchHit>
        {
            new("home-server", "docs/foo.md", "doc", "Foo Title", "", "...foo snippet...", 0.9),
            new("home-server", "docs/bar.md", "doc", "Bar Title", "", "...bar snippet...", 0.5),
        };

        using var server = SearchRouteHost.Build(new ControllableIndexStore(
            (q, src, kind, lim, ct) => Task.FromResult<IReadOnlyList<SearchHit>>(expectedHits)));
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search?q=foo+bar");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        // Results.Json uses camelCase (JsonSerializerDefaults.Web).
        Assert.Equal("foo bar", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("resultCount").GetInt32());

        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());

        var first = results[0];
        Assert.Equal("home-server", first.GetProperty("source").GetString());
        Assert.Equal("docs/foo.md", first.GetProperty("path").GetString());
        Assert.Equal("doc", first.GetProperty("kind").GetString());
        Assert.Equal("Foo Title", first.GetProperty("title").GetString());
        Assert.Contains("snippet", first.GetProperty("snippet").GetString());
        Assert.True(first.GetProperty("score").GetDouble() > 0);
    }

    [Fact]
    public async Task Search_EmptyResultSet_Returns200_WithZeroCount()
    {
        using var server = SearchRouteHost.Build(new ControllableIndexStore(
            (q, src, kind, lim, ct) => Task.FromResult<IReadOnlyList<SearchHit>>(Array.Empty<SearchHit>())));
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search?q=nonexistent+query+term");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        Assert.Equal(0, doc.RootElement.GetProperty("resultCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    // ── source filter forwarding ──────────────────────────────────────────────

    [Fact]
    public async Task Search_WithSourceFilter_ForwardsSourceToStore()
    {
        string? capturedSource = "not-set";
        using var server = SearchRouteHost.Build(new ControllableIndexStore(
            (q, src, kind, lim, ct) =>
            {
                capturedSource = src;
                return Task.FromResult<IReadOnlyList<SearchHit>>(Array.Empty<SearchHit>());
            }));
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        await client.GetAsync("/search?q=anything&source=home-server");

        Assert.Equal("home-server", capturedSource);
    }

    // ── limit forwarding ─────────────────────────────────────────────────────

    [Fact]
    public async Task Search_WithLimit_ForwardsLimitToStore()
    {
        int capturedLimit = -1;
        using var server = SearchRouteHost.Build(new ControllableIndexStore(
            (q, src, kind, lim, ct) =>
            {
                capturedLimit = lim;
                return Task.FromResult<IReadOnlyList<SearchHit>>(Array.Empty<SearchHit>());
            }));
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        await client.GetAsync("/search?q=test&limit=5");

        Assert.Equal(5, capturedLimit);
    }

    // ── store error -> 500 with scrubbed message ──────────────────────────────

    [Fact]
    public async Task Search_StoreThrowsWithSecret_Returns500_WithRedactedError()
    {
        using var server = SearchRouteHost.Build(new LeakingIndexStore());
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search?q=anything");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(LeakingIndexStore.Secret, body);
        Assert.Contains("[redacted]", body);
    }

    // ── tombstone + secret-path seam contract ────────────────────────────────
    //
    // The HTTP route does NOT independently filter tombstoned or secret-path rows.
    // The CONTRACT is that IIndexStore.SearchAsync NEVER returns such rows —
    // this is enforced by the SQL WHERE clause in PostgresIndexStore.SearchAsync.
    // Here we document the seam explicitly: a well-behaved store returns only
    // "clean" rows, and those are the only ones the route serves.

    [Fact]
    public async Task Search_SeamContract_OnlyCleanRowsReachCaller()
    {
        var cleanHit = new SearchHit("repo", "docs/ok.md", "doc", "Good Doc", "", "good snippet", 0.8);
        // The store (correctly) never returns secret-path or tombstoned rows.
        using var server = SearchRouteHost.Build(new ControllableIndexStore(
            (q, src, kind, lim, ct) =>
                Task.FromResult<IReadOnlyList<SearchHit>>(new[] { cleanHit })));
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search?q=good");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        Assert.Equal(1, doc.RootElement.GetProperty("resultCount").GetInt32());
        var path = doc.RootElement.GetProperty("results")[0].GetProperty("path").GetString();
        Assert.DoesNotContain("secrets", path ?? "");
        Assert.DoesNotContain("vault", path ?? "");
    }

    // ── M5-secure: the Bearer-token gate ──────────────────────────────────────
    //
    // Root cause: INDEXER_SEARCH_TOKEN was read NOWHERE in the service; the
    // route was gated only by whether it was mounted at all. These tests pin
    // the fix: no/wrong token => 401 BEFORE the store is ever touched (proven
    // via a store that throws if invoked); correct token => 200.

    [Fact]
    public async Task Search_NoAuthorizationHeader_Returns401_StoreNeverInvoked()
    {
        using var server = SearchRouteHost.Build(new ThrowingIndexStore());
        var client = server.CreateClient(); // no Authorization header at all

        var resp = await client.GetAsync("/search?q=anything");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("unauthorized", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Search_WrongToken_Returns401_StoreNeverInvoked()
    {
        using var server = SearchRouteHost.Build(new ThrowingIndexStore());
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "totally-wrong-token");

        var resp = await client.GetAsync("/search?q=anything");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Search_MalformedAuthorizationHeader_Returns401()
    {
        using var server = SearchRouteHost.Build(new ThrowingIndexStore());
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "test-search-token"); // missing "Bearer " scheme

        var resp = await client.GetAsync("/search?q=anything");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Search_CorrectToken_Returns200_WithHits()
    {
        var expectedHits = new List<SearchHit>
        {
            new("home-server", "docs/foo.md", "doc", "Foo Title", "", "...foo snippet...", 0.9),
        };
        using var server = SearchRouteHost.Build(new ControllableIndexStore(
            (q, src, kind, lim, ct) => Task.FromResult<IReadOnlyList<SearchHit>>(expectedHits)));
        var client = server.CreateClient();
        SearchRouteHost.SetBearer(client, "test-search-token");

        var resp = await client.GetAsync("/search?q=foo");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("resultCount").GetInt32());
    }

    [Fact]
    public void SearchAuth_IsAuthorized_UnitTests()
    {
        // No configured token => fail closed (never silently open), regardless of header.
        Assert.False(SearchAuth.IsAuthorized(null, null));
        Assert.False(SearchAuth.IsAuthorized("Bearer anything", null));
        Assert.False(SearchAuth.IsAuthorized("Bearer anything", ""));

        // Configured token, no/garbage header => unauthorized.
        Assert.False(SearchAuth.IsAuthorized(null, "secret"));
        Assert.False(SearchAuth.IsAuthorized("", "secret"));
        Assert.False(SearchAuth.IsAuthorized("secret", "secret"));           // missing "Bearer " scheme
        Assert.False(SearchAuth.IsAuthorized("Basic secret", "secret"));     // wrong scheme
        Assert.False(SearchAuth.IsAuthorized("Bearer wrong", "secret"));     // mismatched token

        // Configured token, correct header => authorized.
        Assert.True(SearchAuth.IsAuthorized("Bearer secret", "secret"));
        Assert.True(SearchAuth.IsAuthorized("bearer secret", "secret"));     // case-insensitive scheme
    }
}
