using System.Reflection;
using Sinapsi.Indexer;
using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// The publish_learning WRITE tool — the capability the prior port DROPPED, now
/// restored. It validates its inputs (NATS-safe slug/scope, required title+body)
/// and, on the happy path, emits a learning-published event whose subject is
/// env-driven. These pin the validation surface (which returns BEFORE any NATS
/// connect) and the env-driven subject construction, without a live bus.
/// </summary>
public sealed class LearnToolsTests
{
    private static LearnPublisher MakePublisher() =>
        // Default neutral opts; the validation paths reject before any connect, so
        // no bus is contacted in these tests.
        new(NatsConnectionOptions.FromEnvironment());

    private static string? ErrorOf(object result)
    {
        // The tool returns an anonymous { error = "..." } on rejection.
        var prop = result.GetType().GetProperty("error", BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(result) as string;
    }

    [Fact]
    public async Task Rejects_a_non_kebab_slug()
    {
        var r = await LearnTools.PublishLearning(MakePublisher(), slug: "Bad Slug", title: "t", body: "b");
        Assert.Contains("slug", ErrorOf(r));
    }

    [Fact]
    public async Task Rejects_a_non_token_scope()
    {
        var r = await LearnTools.PublishLearning(MakePublisher(), slug: "ok-slug", title: "t", body: "b", scope: "Not A Token");
        Assert.Contains("scope", ErrorOf(r));
    }

    [Fact]
    public async Task Rejects_missing_title_or_body()
    {
        var r1 = await LearnTools.PublishLearning(MakePublisher(), slug: "ok-slug", title: "", body: "b");
        Assert.Contains("title and body", ErrorOf(r1));

        var r2 = await LearnTools.PublishLearning(MakePublisher(), slug: "ok-slug", title: "t", body: "   ");
        Assert.Contains("title and body", ErrorOf(r2));
    }

    [Fact]
    public void Publisher_subject_is_env_driven_with_neutral_default()
    {
        var saved = Environment.GetEnvironmentVariable("LEARN_SUBJECT_PREFIX");
        try
        {
            Environment.SetEnvironmentVariable("LEARN_SUBJECT_PREFIX", null);
            var pub = MakePublisher();
            // Neutral default prefix — no organisation-specific namespace baked in.
            Assert.Equal("events.learn.global.published", pub.SubjectFor("global"));
            Assert.Equal("events.learn.acme.published", pub.SubjectFor("acme"));

            Environment.SetEnvironmentVariable("LEARN_SUBJECT_PREFIX", "myorg.learn");
            var pub2 = MakePublisher();
            Assert.Equal("myorg.learn.global.published", pub2.SubjectFor("global"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEARN_SUBJECT_PREFIX", saved);
        }
    }
}
