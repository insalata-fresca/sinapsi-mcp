using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Nats.Tests;

/// <summary>
/// Compile-time + behavioural parity guard for the worker base classes. A typical
/// consumer subclasses <see cref="JetStreamWorker"/> exactly as below; if any
/// protected member is renamed/removed, this file fails to compile, which is the
/// parity signal we want.
/// </summary>
public sealed class WorkerContractTests
{
    private sealed class SampleJsWorker : JetStreamWorker
    {
        public bool StartCalled;
        public string? LastSubject;

        public SampleJsWorker(NatsHomelabOptions opts)
            : base(opts, NullLogger<SampleJsWorker>.Instance) { }

        protected override string StreamName => "STREAM";
        protected override string DurableName => "durable";
        protected override string FilterSubject => "subject.>";

        // Exercise the overridable delivery policy + start hook.
        protected override ConsumerConfigDeliverPolicy DeliverPolicy => ConsumerConfigDeliverPolicy.LastPerSubject;

        protected override ValueTask OnStartAsync(NatsJSContext js, CancellationToken ct)
        {
            StartCalled = true;
            return ValueTask.CompletedTask;
        }

        protected override ValueTask ProcessAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            LastSubject = subject;
            return ValueTask.CompletedTask;
        }

        public ConsumerConfigDeliverPolicy ExposedDeliverPolicy => DeliverPolicy;
    }

    private sealed class SampleKvWorker : KvWatchWorker
    {
        public byte[]? LastValue;
        public bool AbsentSeen;

        public SampleKvWorker(NatsHomelabOptions opts)
            : base(opts, NullLogger<SampleKvWorker>.Instance) { }

        protected override string Bucket => "bucket";
        protected override string Key => "key";

        protected override ValueTask OnValueAsync(byte[] value, CancellationToken ct)
        {
            LastValue = value;
            return ValueTask.CompletedTask;
        }

        protected override ValueTask OnAbsentAsync(CancellationToken ct)
        {
            AbsentSeen = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void JetStreamWorker_DefaultDeliverPolicy_IsAll()
    {
        // Default on the base is DeliverAll; a subclass can override (see SampleJsWorker).
        var baseLike = new DefaultPolicyWorker(new NatsHomelabOptions());
        Assert.Equal(ConsumerConfigDeliverPolicy.All, baseLike.ExposedDeliverPolicy);
    }

    [Fact]
    public void JetStreamWorker_OverriddenDeliverPolicy_IsHonoured()
    {
        var w = new SampleJsWorker(new NatsHomelabOptions());
        Assert.Equal(ConsumerConfigDeliverPolicy.LastPerSubject, w.ExposedDeliverPolicy);
    }

    [Fact]
    public void JetStreamWorker_ExposesReadyAndCounter_StartingUnready()
    {
        var w = new SampleJsWorker(new NatsHomelabOptions());
        Assert.False(w.Ready);
        Assert.Equal(0, w.EventsProcessed);
    }

    [Fact]
    public void JetStreamWorker_IsAHostedBackgroundService()
    {
        Assert.IsAssignableFrom<Microsoft.Extensions.Hosting.IHostedService>(new SampleJsWorker(new NatsHomelabOptions()));
    }

    [Fact]
    public void KvWatchWorker_ExposesReady_StartingUnready()
    {
        var w = new SampleKvWorker(new NatsHomelabOptions());
        Assert.False(w.Ready);
        Assert.IsAssignableFrom<Microsoft.Extensions.Hosting.IHostedService>(w);
    }

    private sealed class DefaultPolicyWorker : JetStreamWorker
    {
        public DefaultPolicyWorker(NatsHomelabOptions opts)
            : base(opts, NullLogger<DefaultPolicyWorker>.Instance) { }
        protected override string StreamName => "S";
        protected override string DurableName => "d";
        protected override string FilterSubject => "s.>";
        protected override ValueTask ProcessAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ConsumerConfigDeliverPolicy ExposedDeliverPolicy => DeliverPolicy;
    }
}
