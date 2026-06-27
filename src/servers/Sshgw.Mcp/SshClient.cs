using Renci.SshNet;
// The Web SDK's implicit usings pull in Microsoft.AspNetCore.Http.ConnectionInfo,
// which collides with Renci.SshNet.ConnectionInfo. Alias the SSH.NET type.
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace Sshgw.Mcp;

/// <summary>
/// Thin SSH transport over SSH.NET (Renci.SshNet). Stateless: each tool call opens
/// its own short-lived connection and tears it down (matches the stateless HTTP
/// transport in Program.cs — no session pinning to manage).
///
/// SCOPE NOTE: connection caching, host-key pinning, and exact stderr/exit-code
/// shaping are deliberate follow-ups. The security-critical bounds
/// (<see cref="CommandWhitelist"/>, <see cref="ReadFilePolicy"/>) are enforced in
/// <see cref="SshgwTools"/> BEFORE calling in here.
/// </summary>
public sealed class SshClient
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

    public async Task<ExecResult> ExecuteAsync(ServerEntry e, string command, CancellationToken ct)
    {
        using var client = new Renci.SshNet.SshClient(BuildConnectionInfo(e));
        await Task.Run(() => client.Connect(), ct);
        try
        {
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMilliseconds(_opts.CommandTimeoutMs);
            var stdout = await Task.Run(() => cmd.Execute(), ct);
            return new ExecResult(cmd.ExitStatus ?? -1, stdout, cmd.Error);
        }
        finally { client.Disconnect(); }
    }

    /// <summary>Read at most <paramref name="maxBytes"/> from a remote file via
    /// SFTP. Returns the bytes + whether the file was larger (truncated).</summary>
    public async Task<ReadResult> ReadFileAsync(ServerEntry e, string path, int maxBytes, CancellationToken ct)
    {
        using var sftp = new SftpClient(BuildConnectionInfo(e));
        await Task.Run(() => sftp.Connect(), ct);
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
}

public sealed record ExecResult(int ExitCode, string Stdout, string Stderr);
public sealed record ReadResult(byte[] Bytes, long TotalSize, bool Truncated, bool NotFound, bool IsDirectory = false);
