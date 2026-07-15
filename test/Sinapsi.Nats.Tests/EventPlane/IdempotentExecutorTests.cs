using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>C3 item 2 — IDEMPOTENCY + END-TO-END CHANGE-ID. Proves a re-drive after a lost/timed-out
/// ack does not double-apply, and that the change-id is stable/deterministic (docs/64 §3).</summary>
public sealed class IdempotentExecutorTests
{
    [Fact]
    public void ChangeId_IsDeterministic_ForSameIdentity()
    {
        var a = ChangeId.Derive("merge-pr", "ste/sinapsi-mcp#123", "corr-1");
        var b = ChangeId.Derive("merge-pr", "ste/sinapsi-mcp#123", "corr-1");
        Assert.Equal(a, b);
        Assert.StartsWith("chg_", a);
    }

    [Theory]
    [InlineData("deploy", "ste/sinapsi-mcp#123", "corr-1")]
    [InlineData("merge-pr", "ste/sinapsi-mcp#999", "corr-1")]
    [InlineData("merge-pr", "ste/sinapsi-mcp#123", "corr-2")]
    public void ChangeId_Differs_WhenAnyFieldDiffers(string kind, string target, string corr)
    {
        var baseId = ChangeId.Derive("merge-pr", "ste/sinapsi-mcp#123", "corr-1");
        Assert.NotEqual(baseId, ChangeId.Derive(kind, target, corr));
    }

    [Fact]
    public void ChangeId_CannotCollideAcrossFieldBoundaries()
    {
        // Length-prefixing must stop ("ab","c",..) from hashing like ("a","bc",..).
        Assert.NotEqual(
            ChangeId.Derive("ab", "c", "corr"),
            ChangeId.Derive("a", "bc", "corr"));
    }

    [Fact]
    public async Task FirstDrive_RunsEffectExactlyOnce()
    {
        var store = new InMemoryIdempotencyStore();
        var runs = 0;
        var id = ChangeId.Derive("merge-pr", "ste/sinapsi-mcp#1", "corr");

        var result = await IdempotentExecutor.RunOnceAsync(store, id, _ =>
        {
            runs++;
            return ValueTask.FromResult("{\"merged\":true}");
        });

        Assert.True(result.Executed);
        Assert.Equal("{\"merged\":true}", result.ResultJson);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ReDriveAfterLostAck_ReplaysResult_DoesNotDoubleApply()
    {
        // Simulate: attempt #1 actually merged, but the ack was lost. Attempt #2 re-issues the SAME
        // change-id. The effect MUST NOT run a second time; the recorded result is replayed.
        var store = new InMemoryIdempotencyStore();
        var runs = 0;
        var id = ChangeId.Derive("merge-pr", "ste/sinapsi-mcp#1", "corr");

        Func<CancellationToken, ValueTask<string>> effect = _ =>
        {
            runs++;
            return ValueTask.FromResult("{\"merged\":true}");
        };

        var first = await IdempotentExecutor.RunOnceAsync(store, id, effect);   // ack "lost" after this
        var second = await IdempotentExecutor.RunOnceAsync(store, id, effect);  // safe re-drive

        Assert.True(first.Executed);
        Assert.False(second.Executed);                 // replayed, not re-run
        Assert.Equal(first.ResultJson, second.ResultJson);
        Assert.Equal(1, runs);                         // the effect ran exactly ONCE across both drives
    }

    [Fact]
    public async Task ConcurrentInFlightClaim_RefusesRatherThanDoubleApply()
    {
        // Another driver holds the claim (in-flight, not yet applied). We must not run the effect.
        var store = new InMemoryIdempotencyStore();
        var id = ChangeId.Derive("deploy", "target", "corr");
        Assert.True(await store.TryBeginAsync(id)); // a "concurrent" driver claims it first

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await IdempotentExecutor.RunOnceAsync(store, id, _ => ValueTask.FromResult("x")));
    }

    [Fact]
    public async Task EmptyChangeId_IsRejected()
    {
        var store = new InMemoryIdempotencyStore();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await IdempotentExecutor.RunOnceAsync(store, "  ", _ => ValueTask.FromResult("x")));
    }
}
