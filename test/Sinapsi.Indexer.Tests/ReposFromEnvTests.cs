using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// The repo set is built entirely from the environment with neutral defaults —
/// nothing (forge host, owner/repo, branch) is baked in. These pin the env-driven
/// parsing + the wall-clean default (empty repo list until configured).
/// </summary>
public sealed class ReposFromEnvTests
{
    private static T WithEnv<T>(IReadOnlyDictionary<string, string?> env, Func<T> body)
    {
        var keys = new[] { "FORGE_BASE_URL", "INDEXER_REPOS", "INDEXER_REPO_BRANCH", "INDEXER_CACHE_DIR" };
        var saved = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var k in keys) Environment.SetEnvironmentVariable(k, env.TryGetValue(k, out var v) ? v : null);
            return body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void Default_repo_list_is_empty_nothing_baked_in()
    {
        var repos = WithEnv(new Dictionary<string, string?>(), GitSourceScanner.ReposFromEnv);
        Assert.Empty(repos);
    }

    [Fact]
    public void Repos_are_parsed_from_env_into_clone_urls()
    {
        var repos = WithEnv(new Dictionary<string, string?>
        {
            ["FORGE_BASE_URL"] = "https://forge.example.com",
            ["INDEXER_REPOS"] = "docs=acme/docs, learnings=acme/learnings",
            ["INDEXER_REPO_BRANCH"] = "main",
            ["INDEXER_CACHE_DIR"] = "/tmp/cache",
        }, GitSourceScanner.ReposFromEnv);

        Assert.Equal(2, repos.Count);

        var docs = repos.Single(r => r.Source == "docs");
        Assert.Equal("https://forge.example.com/acme/docs.git", docs.Url);
        Assert.Equal("main", docs.Branch);
        Assert.Equal(System.IO.Path.Combine("/tmp/cache", "docs"), docs.CacheDir);

        var learnings = repos.Single(r => r.Source == "learnings");
        Assert.Equal("https://forge.example.com/acme/learnings.git", learnings.Url);
    }

    [Fact]
    public void Trailing_slash_on_base_url_is_normalised()
    {
        var repos = WithEnv(new Dictionary<string, string?>
        {
            ["FORGE_BASE_URL"] = "https://forge.example.com/",
            ["INDEXER_REPOS"] = "docs=acme/docs",
        }, GitSourceScanner.ReposFromEnv);

        Assert.Equal("https://forge.example.com/acme/docs.git", repos.Single().Url);
    }

    [Fact]
    public void Default_base_url_is_a_neutral_example_host()
    {
        var repos = WithEnv(new Dictionary<string, string?>
        {
            ["INDEXER_REPOS"] = "docs=acme/docs",
        }, GitSourceScanner.ReposFromEnv);

        // No real instance baked in — the default is forge.example.com.
        Assert.StartsWith("https://forge.example.com/", repos.Single().Url);
    }
}
