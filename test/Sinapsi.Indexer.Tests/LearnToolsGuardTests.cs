// ---------------------------------------------------------------------------
// LearnToolsGuardTests - the added defence-in-depth caps on publish_learning
// (length caps + control-char rejection on title/body/tags/session_context)
// all short-circuit BEFORE any NATS connect. Complements the existing
// LearnToolsTests (kebab-slug/scope + required title/body).
// The publisher is real but is never contacted: every case here returns from a
// validation guard, so no bus is required (mirrors the existing suite's
// contract that validation returns before connect).
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Sinapsi.Indexer;
using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Indexer.Tests;

public sealed class LearnToolsGuardTests
{
    private static LearnPublisher MakePublisher() =>
        new(NatsConnectionOptions.FromEnvironment());

    [Fact]
    public async Task Rejects_a_control_char_in_the_title()
    {
        // \0 escape, never a literal NUL byte in source.
        var r = await LearnTools.PublishLearning(MakePublisher(), slug: "ok-slug", title: "bad\0title", body: "b");
        Assert.Contains("title", Anon.Error(r));
    }

    [Fact]
    public async Task Rejects_a_newline_in_the_title()
    {
        var r = await LearnTools.PublishLearning(MakePublisher(), slug: "ok-slug", title: "line1\nline2", body: "b");
        Assert.Contains("one-line", Anon.Error(r));
    }

    [Fact]
    public async Task Rejects_an_over_length_body()
    {
        var big = new string('b', IndexerValidation.MaxBodyLength + 1);
        var r = await LearnTools.PublishLearning(MakePublisher(), slug: "ok-slug", title: "t", body: big);
        Assert.Contains("too long", Anon.Error(r));
    }

    [Fact]
    public async Task Allows_markdown_newlines_in_body_but_rejects_a_nul()
    {
        // A NUL in the body is rejected...
        var bad = await LearnTools.PublishLearning(MakePublisher(), slug: "ok-slug", title: "t", body: "good\0bad");
        Assert.Contains("control", Anon.Error(bad));
        // ...but ordinary markdown newlines in a body are NOT a control-char reject
        // (the reject, if any, would come from the downstream publish, not validation).
        // We assert only that the body guard itself accepts markdown newlines.
        Assert.Null(IndexerValidation.ValidateBody("## Claim\n\nline"));
    }

    [Fact]
    public async Task Rejects_too_many_tags()
    {
        var tags = Enumerable.Repeat("t", IndexerValidation.MaxTags + 1).ToArray();
        var r = await LearnTools.PublishLearning(MakePublisher(), slug: "ok-slug", title: "t", body: "b", tags: tags);
        Assert.Contains("too many tags", Anon.Error(r));
    }

    [Fact]
    public async Task Rejects_a_control_char_in_a_tag()
    {
        var r = await LearnTools.PublishLearning(
            MakePublisher(), slug: "ok-slug", title: "t", body: "b", tags: new[] { "ok", "bad\0tag" });
        Assert.Contains("control", Anon.Error(r));
    }

    [Fact]
    public async Task Rejects_a_newline_in_session_context()
    {
        var r = await LearnTools.PublishLearning(
            MakePublisher(), slug: "ok-slug", title: "t", body: "b", session_context: "a\nb");
        Assert.Contains("one-line", Anon.Error(r));
    }
}
