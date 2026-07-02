using Sshgw.Mcp;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// A fake <see cref="SshClient"/> whose transport methods are overridden. It is the
/// test double that lets the tool-level suites drive the guard logic WITHOUT any
/// real SSH I/O:
///   - <see cref="ThrowingTransport"/> throws the instant ANY transport method is
///     reached, so a test proves a guard (validation / whitelist / denylist)
///     short-circuited BEFORE any SSH I/O.
///   - a canned transport returns a scripted result so the surfaced-error /
///     redaction / timeout / upload / symlink paths can be exercised
///     deterministically.
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

    public override Task<ExecResult> ExecuteAsync(ServerEntry e, string command, int? timeoutMs, string? directory, CancellationToken ct)
    {
        Reached = true;
        throw new InvalidOperationException("SSH I/O was reached — a guard failed to short-circuit");
    }

    public override Task<ReadResult> ReadFileAsync(ServerEntry e, string path, int maxBytes, CancellationToken ct)
    {
        Reached = true;
        throw new InvalidOperationException("SSH I/O was reached — a guard failed to short-circuit");
    }

    public override Task<string?> ResolveRealPathAsync(ServerEntry e, string path, CancellationToken ct)
    {
        Reached = true;
        throw new InvalidOperationException("SSH I/O was reached — a guard failed to short-circuit");
    }

    public override Task<UploadResult> UploadAsync(ServerEntry e, string localPath, string remotePath, CancellationToken ct)
    {
        Reached = true;
        throw new InvalidOperationException("SSH I/O was reached — a guard failed to short-circuit");
    }
}

/// <summary>
/// A fake transport that returns scripted results. Used to drive the tool bodies
/// past the guards and exercise the surfaced-stderr redaction, the raw-exit-code
/// verdict, the content pass-through, the upload round-trip, and the symlink
/// re-check. <see cref="ExecCalls"/> records the (command, directory, timeout) each
/// <see cref="ExecuteAsync"/> was invoked with so a test can assert the cd-prefix +
/// per-call timeout wiring.
/// </summary>
internal sealed class CannedTransport : SshClient
{
    private readonly ExecResult? _exec;
    private readonly ReadResult? _read;
    private readonly Exception? _readThrows;
    private readonly UploadResult? _upload;
    private readonly Func<string, string?>? _realPath;

    public List<(string command, int? timeoutMs, string? directory)> ExecCalls { get; } = new();
    public List<(string localPath, string remotePath)> UploadCalls { get; } = new();
    public List<string> RealPathCalls { get; } = new();

    public CannedTransport(
        ExecResult? exec = null,
        ReadResult? read = null,
        Exception? readThrows = null,
        UploadResult? upload = null,
        Func<string, string?>? realPath = null)
        : base(ThrowingTransport.NeutralOpts())
    {
        _exec = exec;
        _read = read;
        _readThrows = readThrows;
        _upload = upload;
        _realPath = realPath;
    }

    public override Task<ExecResult> ExecuteAsync(ServerEntry e, string command, int? timeoutMs, string? directory, CancellationToken ct)
    {
        ExecCalls.Add((command, timeoutMs, directory));
        return Task.FromResult(_exec ?? throw new InvalidOperationException("no scripted ExecResult"));
    }

    public override Task<ReadResult> ReadFileAsync(ServerEntry e, string path, int maxBytes, CancellationToken ct)
    {
        if (_readThrows is not null) throw _readThrows;
        return Task.FromResult(_read ?? throw new InvalidOperationException("no scripted ReadResult"));
    }

    public override Task<string?> ResolveRealPathAsync(ServerEntry e, string path, CancellationToken ct)
    {
        RealPathCalls.Add(path);
        // Default (no realPath func): resolve to the same path (no symlink in play).
        return Task.FromResult(_realPath is null ? path : _realPath(path));
    }

    public override Task<UploadResult> UploadAsync(ServerEntry e, string localPath, string remotePath, CancellationToken ct)
    {
        UploadCalls.Add((localPath, remotePath));
        return Task.FromResult(_upload ?? throw new InvalidOperationException("no scripted UploadResult"));
    }
}
