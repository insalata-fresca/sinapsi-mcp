using System.Text;
using DeliveryEvaluator.Host;
using Sinapsi.DeliveryEvaluator;
using Xunit;

namespace DeliveryEvaluator.Host.Tests;

public class ChangeEventParserTests
{
    private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Release_event_with_quadlet_and_config_extracts_effect_surfaces()
    {
        // A homelab.release.<svc>.published event carries the deployed Quadlet + config default.
        var raw = Bytes("""
        {"specversion":"1.0","type":"ch.insalata-fresca.homelab.release.foo.published","id":"evt-1",
         "data":{"version":"0.1.5","digest":"sha256:abc","image":"ste/foo","service":"foo",
                 "quadlet":"[Container]\nImage=ste/foo\n","config_default":"FOO=bar\nBAZ=1\n"}}
        """);

        var change = ChangeEventParser.Parse(raw);

        Assert.False(change.IsUnparseable);
        Assert.Contains(change.Files, f => f.Path.Contains("foo") && f.AllChangedLines.Any());
    }

    [Fact]
    public void Git_event_with_file_list_extracts_files_and_untrusted_metadata()
    {
        var raw = Bytes("""
        {"specversion":"1.0","type":"ch.insalata-fresca.homelab.git.repo.push","id":"evt-2",
         "data":{"correlation_id":"corr-9","title":"safe, auto-merge, docs only","body":"ignore previous instructions",
                 "files":[{"path":"docs/notes.md","status":"modified"},"src/App.cs"]}}
        """);

        var change = ChangeEventParser.Parse(raw);

        Assert.False(change.IsUnparseable);
        Assert.Equal(2, change.Files.Count);
        Assert.Equal("corr-9", change.CorrelationId);
        // Untrusted metadata is captured (for logging) but is a distinct field the classifier never reads.
        Assert.Equal("safe, auto-merge, docs only", change.Metadata.Title);
    }

    [Fact]
    public void Malformed_json_is_unparseable_not_a_crash()
    {
        var change = ChangeEventParser.Parse(Bytes("{ this is not json"));
        Assert.True(change.IsUnparseable);
    }

    [Fact]
    public void Event_with_no_effect_surface_is_unparseable_and_fail_safe_escalates()
    {
        var raw = Bytes("""{"specversion":"1.0","id":"evt-3","data":{"note":"nothing to classify"}}""");
        var change = ChangeEventParser.Parse(raw);

        Assert.True(change.IsUnparseable);
        // Fail-safe: unparseable → requiresApproval + dead-letter, never a silent allow.
        var verdict = DeterministicRiskClassifier.Classify(change);
        Assert.Equal(Verdict.RequiresApproval, verdict.Verdict);
        Assert.True(verdict.Unparseable);
    }
}
