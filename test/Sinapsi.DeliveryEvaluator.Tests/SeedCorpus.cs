using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Sinapsi.DeliveryEvaluator.Tests;

/// <summary>One labelled scenario from the B1 seed corpus. The label fields
/// (<see cref="CorrectVerdict"/> / <see cref="Tier"/> / <see cref="IsAdversarial"/>) are the answer
/// key — they are WITHHELD from the evaluator (only <see cref="DiffSummary"/> is fed in) exactly as
/// Mission B2 does (home-server <c>datasets/risk-rubric/README.md</c>).</summary>
public sealed class SeedCorpusScenario
{
    public string Id { get; set; } = "";
    public string DiffSummary { get; set; } = "";
    public string Tier { get; set; } = "";
    public string CorrectVerdict { get; set; } = "";
    public string Rationale { get; set; } = "";
    public bool IsAdversarial { get; set; }
}

internal sealed class SeedCorpusFile
{
    public Dictionary<string, object>? Meta { get; set; }
    public List<SeedCorpusScenario>? Scenarios { get; set; }
}

/// <summary>Loads the vendored B1 answer key.</summary>
public static class SeedCorpus
{
    public static IReadOnlyList<SeedCorpusScenario> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "seed-corpus.yaml");
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var file = deserializer.Deserialize<SeedCorpusFile>(yaml);
        return file?.Scenarios ?? new List<SeedCorpusScenario>();
    }
}
