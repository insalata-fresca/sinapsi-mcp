using Renci.SshNet;
using Renci.SshNet.Common;
// The Web SDK's implicit usings pull in Microsoft.AspNetCore.Http.ConnectionInfo,
// which collides with Renci.SshNet.ConnectionInfo. Alias the SSH.NET type.
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace Sshgw.Mcp;

/// <summary>
/// Thin SSH transport over SSH.NET (Renci.SshNet). Stateless: each tool call opens
/// its own short-lived connection and tears it down (matches the stateless HTTP
/// transport in Program.cs — no session pinning to manage).
///
/// SECURITY: every connection is host-key pinned via <see cref="HostKeyPolicy"/>
/// (wired into SSH.NET's <c>HostKeyReceived</c> event) — a spoofed / unknown host
/// key is rejected before authentication, closing the MITM hole that SSH.NET leaves
/// open by default (it otherwise trusts any key). The command / path security bounds
/// (<see cref="CommandWhitelist"/>, <see cref="ReadFilePolicy"/>) are enforced in
/// <see cref="SshgwTools"/> BEFORE calling in here.
///
/// <para>
/// The transport methods (<see cref="ExecuteAsync"/>, <see cref="ReadFileAsync"/>,
/// <see cref="UploadAsync"/>, <see cref="ResolveRealPathAsync"/>) are
/// <c>virtual</c> and the class is unsealed <b>solely</b> so a test can inject a
/// fake transport (proving the tool guards short-circuit BEFORE any SSH I/O, and
/// exercising the tool bodies against scripted results). There is no production
/// subclass — Program.cs constructs the concrete class — and the signatures, DI
/// wiring, and behaviour are unchanged.
/// </para>
/// </summary>
public class SshClient
{
    private readonly SshgwOptions _opts;
    public SshClient(SshgwOptions opts) => _opts = opts;

    private ConnectionInfo BuildConnectionInfo(ServerEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.PrivateKey))
            throw new InvalidOperationException($"server '{e.Name}' has no privateKey");
        var keyFile = new PrivateKeyFile(e.PrivateKey);
        var auth = new PrivateKeyAuthenticationMethod(e.Username, keyFile);
        return new ConnectionInfo(e.Host, e.Port, e.Username, auth)
        {
            Timeout = TimeSpan.FromMilliseconds(_opts.ConnectTimeoutMs),
        };
    }

    /// <summary>Build a <see cref="HostKeyPolicy"/> for a server from its configured
    /// fingerprint(s) plus the global "require pin" posture.</summary>
    private HostKeyPolicy HostKeyPolicyFor(ServerEntry e) =>
        new(e.HostKeyFingerprints, _opts.RequireHostKeyPin);

    /// <summary>Attach the host-key pin check to a client's
    /// <c>HostKeyReceived</c> event. On an untrusted key we set
    /// <see cref="HostKeyEventArgs.CanTrust"/> = false so SSH.NET aborts the
    /// handshake, and record the presented fingerprint so the caller can surface a
    /// precise rejection.</summary>
    private static void WireHostKeyCheck(BaseClient client, HostKeyPolicy policy, out Func<string?> rejection)
    {
        string? rejectFp = null;
        rejection = () => rejectFp;
        client.HostKeyReceived += (_, args) =>
        {
            if (policy.IsTrusted(args.HostKey))
            {
                args.CanTrust = true;
            }
            else
            {
                args.CanTrust = false;
                rejectFp = HostKeyPolicy.FingerprintOf(args.HostKey);
            }
        };
    }

    public virtual async Task<ExecResult> ExecuteAsync(ServerEntry e, string command, int? timeoutMs, string? directory, CancellationToken ct)
    {
        // A working directory, when supplied, is pre-validated by the tool layer to
        // be an absolute path free of shell metacharacters, so prefixing it with a
        // POSIX `cd -- <dir> &&` cannot inject a second command. The `--` stops the
        // dir being taken as a cd flag; the no-metachar guard stops it terminating
        // the cd and starting a new command.
        var effective = string.IsNullOrEmpty(directory)
            ? command
            : $"cd -- {directory} && {command}";

        // The SSH connect timeout is carried on ConnectionInfo.Timeout
        // (BuildConnectionInfo); only the per-command timeout is per-call here.
        var cmdTimeout = TimeSpan.FromMilliseconds(ClampCommandTimeout(timeoutMs));

        using var client = new Renci.SshNet.SshClient(BuildConnectionInfo(e));
        WireHostKeyCheck(client, HostKeyPolicyFor(e), out var rejection);
        await ConnectOrThrow(client, () => client.Connect(), rejection, e, ct);
        try
        {
            using var cmd = client.CreateCommand(effective);
            cmd.CommandTimeout = cmdTimeout;
            var stdout = await Task.Run(() => cmd.Execute(), ct);
            return new ExecResult(cmd.ExitStatus ?? -1, stdout, cmd.Error);
        }
        finally { client.Disconnect(); }
    }

    /// <summary>Read at most <paramref name="maxBytes"/> from a remote file via
    /// SFTP. Returns the bytes + whether the file was larger (truncated).</summary>
    public virtual async Task<ReadResult> ReadFileAsync(ServerEntry e, string path, int maxBytes, CancellationToken ct)
    {
        using var sftp = new SftpClient(BuildConnectionInfo(e));
        WireHostKeyCheck(sftp, HostKeyPolicyFor(e), out var rejection);
        await ConnectOrThrow(sftp, () => sftp.Connect(), rejection, e, ct);
        try
        {
            if (!sftp.Exists(path)) return new ReadResult(Array.Empty<byte>(), 0, false, NotFound: true);
            var attrs = sftp.GetAttributes(path);
            if (attrs.IsDirectory) return new ReadResult(Array.Empty<byte>(), 0, false, NotFound: false, IsDirectory: true);

            using var remote = sftp.OpenRead(path);
            using var buf = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            bool truncated = false;
            while ((read = await remote.ReadAsync(chunk, ct)) > 0)
            {
                int room = maxBytes - (int)buf.Length;
                if (read > room) { buf.Write(chunk, 0, room); truncated = true; break; }
                buf.Write(chunk, 0, read);
            }
            return new ReadResult(buf.ToArray(), attrs.Size, truncated, NotFound: false);
        }
        finally { sftp.Disconnect(); }
    }

    /// <summary>Resolve a remote path to its real (symlink-followed) canonical target
    /// via <c>readlink -f</c> over SSH, so the file policy can be re-applied to the
    /// REAL target and a symlink under an allowed dir cannot smuggle out a secret.
    /// Returns the trimmed absolute path, or null when it could not be resolved.</summary>
    public virtual async Task<string?> ResolveRealPathAsync(ServerEntry e, string path, CancellationToken ct)
    {
        // `readlink -f <path>` (coreutils) canonicalises symlinks + '.'/'..' and
        // prints the absolute real path. The path is POSIX single-quoted (and any
        // embedded quote escaped) so that spaces / shell metacharacters in a
        // legitimately-odd filename are passed as ONE literal argument and can never
        // inject a second command. Control chars / NUL were already rejected upstream.
        var probe = $"readlink -f -- {ShellSingleQuote(path)}";

        using var client = new Renci.SshNet.SshClient(BuildConnectionInfo(e));
        WireHostKeyCheck(client, HostKeyPolicyFor(e), out var rejection);
        await ConnectOrThrow(client, () => client.Connect(), rejection, e, ct);
        try
        {
            using var cmd = client.CreateCommand(probe);
            cmd.CommandTimeout = TimeSpan.FromMilliseconds(ClampCommandTimeout(null));
            var stdout = await Task.Run(() => cmd.Execute(), ct);
            if ((cmd.ExitStatus ?? -1) != 0) return null;
            var resolved = stdout.Trim();
            return string.IsNullOrEmpty(resolved) ? null : resolved;
        }
        finally { client.Disconnect(); }
    }

    /// <summary>Upload a local file to a remote path via SFTP (write; elevated). The
    /// remote path is NOT whitelist-gated — upload is an elevated write tool by
    /// design. Host-key pinning still applies. Returns the number of bytes sent.</summary>
    public virtual async Task<UploadResult> UploadAsync(ServerEntry e, string localPath, string remotePath, CancellationToken ct)
    {
        if (!File.Exists(localPath))
            return new UploadResult(0, false, NotFound: true);

        using var sftp = new SftpClient(BuildConnectionInfo(e));
        WireHostKeyCheck(sftp, HostKeyPolicyFor(e), out var rejection);
        await ConnectOrThrow(sftp, () => sftp.Connect(), rejection, e, ct);
        try
        {
            await using var local = File.OpenRead(localPath);
            long size = local.Length;
            // canOverride: true — the tool layer owns the overwrite contract; SFTP
            // itself truncates/creates the destination.
            await Task.Run(() => sftp.UploadFile(local, remotePath, canOverride: true, uploadCallback: null), ct);
            return new UploadResult(size, true, NotFound: false);
        }
        finally { sftp.Disconnect(); }
    }

    /// <summary>POSIX single-quote a string so it is one literal shell argument:
    /// wrap in <c>'…'</c> and replace each embedded <c>'</c> with <c>'\''</c>. This is
    /// the standard, safe way to hand an arbitrary value to a shell — nothing inside
    /// single quotes is special except the single quote itself.</summary>
    internal static string ShellSingleQuote(string s) =>
        "'" + s.Replace("'", "'\\''") + "'";

    private int ClampCommandTimeout(int? timeoutMs)
    {
        // Per-call timeout, clamped to [1, hard cap]. A caller-omitted value falls
        // back to the configured default; a caller value above the hard ceiling is
        // clamped DOWN to it (never honoured beyond the cap).
        var requested = timeoutMs ?? _opts.CommandTimeoutMs;
        return Math.Clamp(requested, 1, SshgwOptions.MaxCommandTimeoutMs);
    }

    /// <summary>Run the (synchronous) SSH.NET connect on a worker thread, then map an
    /// abort caused by our host-key pin rejection into a precise
    /// <see cref="HostKeyRejectedException"/> (SSH.NET otherwise surfaces a generic
    /// connection failure). Any other failure propagates unchanged.</summary>
    private static async Task ConnectOrThrow(BaseClient client, Action connect, Func<string?> rejection, ServerEntry e, CancellationToken ct)
    {
        try
        {
            await Task.Run(connect, ct);
        }
        catch (Exception) when (rejection() is { } fp)
        {
            throw new HostKeyRejectedException(e.Name, fp);
        }

        // Defensive: if the handshake somehow completed but our pin marked the key
        // untrusted, refuse to proceed.
        if (rejection() is { } postFp)
        {
            try { client.Disconnect(); } catch { /* best effort */ }
            throw new HostKeyRejectedException(e.Name, postFp);
        }
    }
}

/// <summary>Thrown when a server's presented host key does not match its configured
/// pin (or when pinning is globally required and the server has no pin). Carries the
/// presented fingerprint so the tool layer can surface a precise, non-leaky reason.</summary>
public sealed class HostKeyRejectedException : Exception
{
    public string ServerName { get; }
    public string PresentedFingerprint { get; }

    public HostKeyRejectedException(string serverName, string presentedFingerprint)
        : base($"host-key verification failed for '{serverName}': presented {presentedFingerprint} is not trusted")
    {
        ServerName = serverName;
        PresentedFingerprint = presentedFingerprint;
    }
}

public sealed record ExecResult(int ExitCode, string Stdout, string Stderr);
public sealed record ReadResult(byte[] Bytes, long TotalSize, bool Truncated, bool NotFound, bool IsDirectory = false);
public sealed record UploadResult(long BytesSent, bool Ok, bool NotFound);
