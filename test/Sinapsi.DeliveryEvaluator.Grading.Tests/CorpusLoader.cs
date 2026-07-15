using Sinapsi.DeliveryEvaluator.Grading;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Sinapsi.DeliveryEvaluator.Grading.Tests;

/// <summary>Loads the vendored B1 answer key (YAML) into <see cref="LabelledScenario"/> rows — the
/// grader's substrate. Mirrors the C1 corpus-loader (snake_case → PascalCase, ignore extra keys).</summary>
internal static class CorpusLoader
{
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string DiffSummary { get; set; } = "";
        public string Tier { get; set; } = "";
        public string CorrectVerdict { get; set; } = "";
        public string Rationale { get; set; } = "";
        public bool IsAdversarial { get; set; }
    }

    private sealed class File_
    {
        public Dictionary<string, object>? Meta { get; set; }
        public List<Row>? Scenarios { get; set; }
    }

    public static IReadOnlyList<LabelledScenario> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "seed-corpus.yaml");
        var yaml = System.IO.File.ReadAllText(path);
        var de = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var file = de.Deserialize<File_>(yaml);
        return (file?.Scenarios ?? new List<Row>())
            .Select(r => new LabelledScenario(r.Id, r.DiffSummary, r.Tier, r.CorrectVerdict, r.IsAdversarial))
            .ToList();
    }
}
