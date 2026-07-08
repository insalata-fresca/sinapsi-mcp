using Xunit;

namespace Cervello.Enrichment.Host.Tests;

/// <summary>
/// The host's drain-loop config is env-driven and FAIL-CLOSED: a bad numeric/range value throws at
/// startup naming the var; defaults are the documented neutral values. (The ENGINE config's own
/// fail-closed + escalate-only defaults are covered by EnrichmentCompositionTests in the engine suite.)
/// </summary>
public sealed class HostConfigTests
{
    private static HostConfig From(params (string, string?)[] kv) =>
        HostConfig.From(new Dictionary<string, string?>(kv.Select(p => new KeyValuePair<string, string?>(p.Item1, p.Item2))));

    [Fact]
    public void Defaults_are_the_documented_neutral_values()
    {
        var cfg = From();
        Assert.Equal(30, cfg.PollIntervalSeconds);
        Assert.Equal(16, cfg.BatchSize);
        Assert.Equal("0.0.0.0", cfg.HealthHost);
        Assert.Equal(8147, cfg.HealthPort); // one above the Watcher's 8146
    }

    [Fact]
    public void Overrides_are_honoured()
    {
        var cfg = From(
            ("CERVELLO_ENRICHMENT_POLL_INTERVAL_SECONDS", "45"),
            ("CERVELLO_ENRICHMENT_BATCH_SIZE", "8"),
            ("CERVELLO_ENRICHMENT_HEALTH_PORT", "9200"));
        Assert.Equal(45, cfg.PollIntervalSeconds);
        Assert.Equal(8, cfg.BatchSize);
        Assert.Equal(9200, cfg.HealthPort);
    }

    [Theory]
    [InlineData("CERVELLO_ENRICHMENT_POLL_INTERVAL_SECONDS", "2")]     // below min 5
    [InlineData("CERVELLO_ENRICHMENT_POLL_INTERVAL_SECONDS", "99999")] // above max 3600
    [InlineData("CERVELLO_ENRICHMENT_POLL_INTERVAL_SECONDS", "sixty")] // non-numeric
    [InlineData("CERVELLO_ENRICHMENT_BATCH_SIZE", "0")]                // below min 1
    [InlineData("CERVELLO_ENRICHMENT_HEALTH_PORT", "70000")]           // above 65535
    public void A_bad_value_throws_naming_the_var(string var, string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => From((var, bad)));
        Assert.Contains(var, ex.Message);
    }
}
