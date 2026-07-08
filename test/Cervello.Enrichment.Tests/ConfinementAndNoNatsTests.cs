using Cervello.Enrichment.Pipeline.Stages;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Confinement invariants as binary properties (DESIGN §7 "No shared NATS"; E0.5 §7). The
/// enrichment engine assembly references NO NATS client and NO shared Sinapsi.Nats lib — the
/// cervello data plane never touches a shared subject. (Live audio/embedding-in-git confinement
/// is E2a/E3 integration territory; here we assert the offline binary property.)
/// </summary>
public sealed class ConfinementAndNoNatsTests
{
    [Fact]
    public void Enrichment_assembly_references_no_nats_client_at_all()
    {
        var asm = typeof(DiarizeEmbedStage).Assembly;

        var referenced = asm.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        foreach (var name in referenced)
        {
            Assert.False(name.Contains("Sinapsi.Nats", StringComparison.OrdinalIgnoreCase),
                $"unexpected reference to {name}");
            Assert.False(name.StartsWith("NATS.", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("NATS", StringComparison.OrdinalIgnoreCase),
                $"unexpected NATS client reference: {name}");
        }

        foreach (var t in asm.GetTypes())
            Assert.False((t.Namespace ?? "").Contains("NATS", StringComparison.OrdinalIgnoreCase),
                $"type {t.FullName} unexpectedly in a NATS namespace");
    }
}
