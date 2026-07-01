using Sshgw.Mcp;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// A fake <see cref="SshClient"/> whose two transport methods are overridden. It
/// is the test double that lets the tool-level suites drive the guard logic
/// WITHOUT any real SSH I/O:
///   - <see cref="ThrowingTransport"/> throws the instant either transport method
///     is reached, so a test proves a guard (validation / whitelist / denylist)
///     short-circuited BEFORE any SSH I/O.
///   - a canned transport returns a scripted <see cref="ExecResult"/> /
///     <see cref="ReadResult"/> so the surfaced-error / redaction / timeout paths
///     can be exercised deterministically.
///
/// This is why <see cref="SshClient"/> is unsealed with virtual transport methods:
/// injection for tests only. There is no production subclass.
/// </summary>
internal sealed class ThrowingTransport : SshClient
{
    public bool Reached { get; private set; }

    // A neutral options record — never used, because the base ctor only stores it
    // and the overrides below never touch the real transport.
    public ThrowingTransport() : base(NeutralOpts()) { }

    internal static SshgwOptions NeutralOpts() => new(
        ServerRegistryPath: "/etc/sshgw/servers.json",
        ConnectTimeoutMs: 10_000,
        CommandTimeoutMs: 30_000,
        ReadFileDefaultMaxBytes: 262_144,
        ReadFileHardMaxBytes: 2_097_152);

    public override Task<ExecResult> ExecuteAsync(ServerEntry e, string command, CancellationToken ct)
    {
        Reached = true;
        throw new InvalidOperationException("SSH I/O was reached — a guard failed to short-circuit");
    }

    public override Task<ReadResult> ReadFileAsync(ServerEntry e, string path, int maxBytes, CancellationToken ct)
    {
        Reached = true;
        throw new InvalidOperationException("SSH I/O was reached — a guard failed to short-circuit");
    }
}

/// <summary>
/// A fake transport that returns a scripted <see cref="ExecResult"/> from
/// <see cref="ExecuteAsync"/> (and a scripted <see cref="ReadResult"/> from
/// <see cref="ReadFileAsync"/>). Used to drive the tool bodies past the guards and
/// exercise the surfaced-stderr redaction, the raw-exit-code verdict, and the
/// content pass-through.
/// </summary>
internal sealed class CannedTransport : SshClient
{
    private readonly ExecResult? _exec;
    private readonly ReadResult? _read;
    private readonly Exception? _readThrows;

    public CannedTransport(ExecResult? exec = null, ReadResult? read = null, Exception? readThrows = null)
        : base(ThrowingTransport.NeutralOpts())
    {
        _exec = exec;
        _read = read;
        _readThrows = readThrows;
    }

    public override Task<ExecResult> ExecuteAsync(ServerEntry e, string command, CancellationToken ct) =>
        Task.FromResult(_exec ?? throw new InvalidOperationException("no scripted ExecResult"));

    public override Task<ReadResult> ReadFileAsync(ServerEntry e, string path, int maxBytes, CancellationToken ct)
    {
        if (_readThrows is not null) throw _readThrows;
        return Task.FromResult(_read ?? throw new InvalidOperationException("no scripted ReadResult"));
    }
}
