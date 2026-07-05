using System.Reflection;
using Cervello.Watcher.Normalize;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// recording-normalize — "Local ready-for-enrichment marker" + invariant 3
/// (the binary references NO NATS client; the ready signal is local).
/// </summary>
public sealed class ReadyMarkerAndNoNatsTests
{
    // ---- Scenario: Ready marker is local ----
    [Fact]
    public void Ready_marker_is_a_local_file_idempotent()
    {
        using var ws = new TempWorkspace();
        var marker = new ReadyMarker(ws.InboxDir);

        Assert.False(marker.IsMarked("20260705-foo"));
        Assert.True(marker.Mark("20260705-foo"));   // newly created
        Assert.True(marker.IsMarked("20260705-foo"));
        Assert.True(File.Exists(marker.MarkerPath("20260705-foo")));
        Assert.False(marker.Mark("20260705-foo"));  // idempotent — already marked
        // The marker is a local file under the inbox dir, nothing more.
        Assert.StartsWith(Path.GetFullPath(ws.InboxDir), Path.GetFullPath(marker.MarkerPath("20260705-foo")));
    }

    // ---- Scenario: No cervello data on shared NATS (invariant 3, binary property) ----
    [Fact]
    public void Watcher_assembly_references_no_nats_client_at_all()
    {
        var asm = typeof(Cervello.Watcher.WatchWorker).Assembly;

        // No referenced assembly is a NATS client or the shared Sinapsi.Nats lib.
        var referenced = asm.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        foreach (var name in referenced)
        {
            Assert.False(name.Contains("Sinapsi.Nats", StringComparison.OrdinalIgnoreCase),
                $"unexpected reference to {name}");
            // NATS.Client / NATS.Net both start with the "NATS." assembly prefix.
            Assert.False(name.StartsWith("NATS.", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("NATS", StringComparison.OrdinalIgnoreCase),
                $"unexpected NATS client reference: {name}");
        }
        // And no loaded type in the assembly lives in a NATS namespace.
        foreach (var t in asm.GetTypes())
            Assert.False((t.Namespace ?? "").Contains("NATS", StringComparison.OrdinalIgnoreCase),
                $"type {t.FullName} unexpectedly in a NATS namespace");
    }
}
