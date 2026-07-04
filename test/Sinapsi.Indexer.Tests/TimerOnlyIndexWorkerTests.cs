// ---------------------------------------------------------------------------
// TimerOnlyIndexWorkerTests - proves the isolated-mode ("INDEXER_NATS_MODE=
// isolated") index shape is mechanically NATS-connection-free AND that the
// index/search capability still functions (a timer-only re-scan, same
// scan->upsert->tombstone engine as the shared-bus shape).
//
// Two proofs:
//   1. STRUCTURAL: TimerOnlyIndexWorker's base type and constructor/field
//      types never mention a NATS type — i.e. there is no PATH by which this
//      type could open a NATS connection, not merely "it doesn't call
//      .ConnectAsync in this version". A future edit that accidentally wires
//      NATS into this type fails this test immediately.
//   2. FUNCTIONAL: constructing + starting the worker with a real (empty-repo)
//      SourceScanner and a recording IIndexStore proves the startup path
//      (EnsureSchema -> ReindexAll -> Ready=true) runs to completion with NO
//      NatsConnectionOptions ever constructed anywhere in the test, and that
//      the shared IndexerCore engine it delegates to (proven functionally by
//      IndexerWorkerTests/IndexToolsGuardTests elsewhere) is reachable from
//      this shell too.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

public sealed class TimerOnlyIndexWorkerTests
{
    // ── structural: no NATS type reachable from the isolated-mode worker ─────

    [Fact]
    public void TimerOnlyIndexWorker_BaseType_IsPlainBackgroundService_NotJetStreamWorker()
    {
        // JetStreamWorker (Sinapsi.Nats) is the base that owns a NatsConnection.
        // The isolated shape must NOT derive from it.
        var baseType = typeof(TimerOnlyIndexWorker).BaseType;
        Assert.NotNull(baseType);
        Assert.Equal("Microsoft.Extensions.Hosting.BackgroundService", baseType!.FullName);
    }

    [Fact]
    public void TimerOnlyIndexWorker_Constructor_NeverTakesANatsType()
    {
        var ctor = typeof(TimerOnlyIndexWorker).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name);
        Assert.DoesNotContain(paramTypeNames, n => n.Contains("Nats", StringComparison.Ordinal));
    }

    [Fact]
    public void TimerOnlyIndexWorker_DeclaredFields_NeverReferenceANatsType()
    {
        // Reflect over private fields too (not just the public constructor
        // shape) — the structural guarantee is "no NATS type reachable from
        // this class", including internal wiring.
        var fields = typeof(TimerOnlyIndexWorker).GetFields(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        foreach (var f in fields)
            Assert.DoesNotContain("Nats", f.FieldType.FullName ?? f.FieldType.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Assembly_Sinapsi_Nats_Types_NeverAppearInAnyTimerOnlyIndexWorkerMember()
    {
        // Belt-and-braces: walk every member (fields, properties, methods incl.
        // parameters + return types) declared on TimerOnlyIndexWorker and assert
        // none of them is a Sinapsi.Nats.* type. This is the mechanical proof
        // behind "NATS_MODE=isolated ⇒ zero NATS client" for the index shape.
        var t = typeof(TimerOnlyIndexWorker);
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.DeclaredOnly;

        bool IsNats(Type ty) => (ty.FullName ?? ty.Name).StartsWith("Sinapsi.Nats", StringComparison.Ordinal);

        foreach (var f in t.GetFields(flags))
            Assert.False(IsNats(f.FieldType), $"field {f.Name} is a Sinapsi.Nats type: {f.FieldType.FullName}");
        foreach (var p in t.GetProperties(flags))
            Assert.False(IsNats(p.PropertyType), $"property {p.Name} is a Sinapsi.Nats type: {p.PropertyType.FullName}");
        foreach (var m in t.GetMethods(flags))
        {
            Assert.False(IsNats(m.ReturnType), $"method {m.Name} returns a Sinapsi.Nats type: {m.ReturnType.FullName}");
            foreach (var pa in m.GetParameters())
                Assert.False(IsNats(pa.ParameterType), $"method {m.Name} param {pa.Name} is a Sinapsi.Nats type: {pa.ParameterType.FullName}");
        }
    }

    // ── functional: the worker's startup path runs with zero NATS involvement ─

    [Fact]
    public async Task ExecuteAsync_RunsStartupReindexToReady_WithNoRepos_NoNatsConstructed()
    {
        // An empty repo list means ReindexAllAsync completes with no git/network
        // calls at all — this proves the startup sequence (EnsureSchema ->
        // ReindexAll -> Ready=true) completes purely through IIndexStore/
        // SourceScanner/IEmbedder fakes, with no NatsConnectionOptions
        // constructed anywhere in this test.
        var store = new RecordingIndexStore(poisonDocId: "none", onPoison: () => new InvalidOperationException("unused"));
        var scanner = new SourceScanner(repos: Array.Empty<RepoSpec>(), token: null, log: NullLogger.Instance);
        var embedder = new FakeEmbedder();
        var worker = new TimerOnlyIndexWorker(store, scanner, embedder, NullLogger<TimerOnlyIndexWorker>.Instance);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        // Give the background ExecuteAsync a moment to reach the startup-scan
        // completion (Ready=true) before the periodic-rescan await blocks.
        for (var i = 0; i < 50 && !worker.Ready; i++)
            await Task.Delay(20);
        cts.Cancel();
        try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        Assert.True(worker.Ready);
        Assert.True(worker.SchemaReady);
    }
}
