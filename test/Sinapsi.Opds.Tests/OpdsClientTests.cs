using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Sinapsi.Opds;
using Sinapsi.Opds.Models;
using Xunit;

namespace Sinapsi.Opds.Tests;

/// <summary>
/// OpdsClient tests over a fake HttpMessageHandler (no network): pagination
/// following rel="next", HTTP Basic vs anonymous auth-header, EPUB download +
/// size cap, and the Diff/Snapshot helpers.
/// </summary>
public class OpdsClientTests
{
    /// <summary>Records requests + serves canned responses keyed by request path.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }

        public static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/atom+xml"),
        };
    }

    [Fact]
    public async Task EnumerateAll_follows_pagination_and_yields_all_entries()
    {
        var handler = new FakeHandler(req =>
        {
            var q = req.RequestUri!.Query;
            return q.Contains("page=2")
                ? FakeHandler.Xml(OpdsFeedParserTests.AcqPage2Xml)
                : FakeHandler.Xml(OpdsFeedParserTests.AcqPage1Xml);
        });
        var client = new OpdsClient(new HttpClient(handler));

        var entries = await client.EnumerateAllAsync("http://library.test:6060/opds/books?page=1");

        Assert.Equal(2, entries.Count);
        Assert.Equal("urn:isbn:9990000000001", entries[0].Id);
        Assert.Equal("urn:isbn:9990000000002", entries[1].Id);
        // Two pages were fetched (page=1 then the resolved rel="next" page=2).
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(handler.Requests, r => r.RequestUri!.Query.Contains("page=2"));
    }

    [Fact]
    public async Task Sends_basic_auth_header_when_configured()
    {
        var handler = new FakeHandler(_ => FakeHandler.Xml(OpdsFeedParserTests.AcqPage2Xml));
        var options = new OpdsClientOptions { Username = "opdsuser", Password = "s3cr3t" };
        var client = new OpdsClient(new HttpClient(handler), options);

        await client.FetchFeedAsync("http://library.test:6060/opds/books");

        var auth = handler.Requests.Single().Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth!.Scheme);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("opdsuser:s3cr3t"));
        Assert.Equal(expected, auth.Parameter);
        // And the options helper builds the identical header.
        Assert.Equal(auth.ToString(), options.BuildBasicAuthHeader()!.ToString());
    }

    [Fact]
    public async Task Sends_no_auth_header_when_anonymous()
    {
        var handler = new FakeHandler(_ => FakeHandler.Xml(OpdsFeedParserTests.AcqPage2Xml));
        var client = new OpdsClient(new HttpClient(handler)); // no options => anonymous

        await client.FetchFeedAsync("http://library.test:6060/opds/books");

        Assert.Null(handler.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public void BasicAuth_header_null_when_no_username()
    {
        Assert.Null(new OpdsClientOptions().BuildBasicAuthHeader());
        Assert.Null(new OpdsClientOptions { Password = "x" }.BuildBasicAuthHeader());
        Assert.NotNull(new OpdsClientOptions { Username = "u" }.BuildBasicAuthHeader());
    }

    [Fact]
    public async Task Downloads_epub_bytes_with_auth()
    {
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 }; // "PK.." zip magic
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var client = new OpdsClient(new HttpClient(handler),
            new OpdsClientOptions { Username = "u", Password = "p" });

        var link = new OpdsLink("http://library.test:6060/dl/1.epub", "application/epub+zip",
            "http://opds-spec.org/acquisition");
        var bytes = await client.DownloadAsync(link);

        Assert.Equal(payload, bytes);
        Assert.NotNull(handler.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public async Task Download_rejects_response_over_size_cap_via_content_length()
    {
        var handler = new FakeHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[10]),
            };
            resp.Content.Headers.ContentLength = 10_000; // declares oversize
            return resp;
        });
        var client = new OpdsClient(new HttpClient(handler),
            new OpdsClientOptions { MaxDownloadBytes = 100 });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DownloadAsync("http://library.test:6060/dl/big.epub"));
    }

    [Fact]
    public async Task Fetch_throws_on_401_unauthorized()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new OpdsClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.FetchFeedAsync("http://library.test:6060/opds"));
    }

    // --- Diff / Snapshot ---------------------------------------------------

    private static OpdsEntry Entry(string id, DateTimeOffset? updated) =>
        new() { Id = id, Title = id, Updated = updated };

    [Fact]
    public void Diff_classifies_new_changed_removed()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddDays(1);

        var previousEntries = new List<OpdsEntry>
        {
            Entry("a", t0),  // will be unchanged
            Entry("b", t0),  // will change (updated advances)
            Entry("c", t0),  // will be removed
        };
        var previous = OpdsClient.Snapshot(previousEntries);

        var current = new List<OpdsEntry>
        {
            Entry("a", t0),  // unchanged
            Entry("b", t1),  // changed
            Entry("d", t0),  // new
        };

        var diff = OpdsClient.Diff(previous, current);

        Assert.Equal(new[] { "d" }, diff.New.Select(e => e.Id));
        Assert.Equal(new[] { "b" }, diff.Changed.Select(e => e.Id));
        Assert.Equal(new[] { "c" }, diff.Removed);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void Diff_empty_when_nothing_changed()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entries = new List<OpdsEntry> { Entry("a", t0), Entry("b", t0) };
        var diff = OpdsClient.Diff(OpdsClient.Snapshot(entries), entries);
        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Diff_uses_id_as_token_when_updated_absent()
    {
        var entries = new List<OpdsEntry> { Entry("a", null) };
        var snap = OpdsClient.Snapshot(entries);
        Assert.Equal("a", snap["a"]);
        // Same id, still no updated => not changed.
        Assert.True(OpdsClient.Diff(snap, entries).IsEmpty);
    }
}
