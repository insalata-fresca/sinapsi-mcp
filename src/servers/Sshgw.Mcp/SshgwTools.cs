using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Sshgw.Mcp;

/// <summary>
/// The sshgw tool surface. The read tools (list-servers / execute-command /
/// read_file) carry their own in-MCP safety bounds (CommandWhitelist,
/// ReadFilePolicy) and enforce them HERE, before any SSH I/O — those bounds are
/// the boundary that keeps a read from running an unlisted command or returning a
/// secret. <c>upload</c> is a write tool.
///
/// <para>
/// Every tool that takes user input runs <see cref="SshgwValidation"/> at the TOP,
/// BEFORE the CommandWhitelist / ReadFilePolicy bounds and BEFORE any SSH I/O, so a
/// malformed connection name / command / path is rejected with a structured error
/// (never an exception, never a leak) before a connection is even attempted.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class SshgwTools
{
    [McpServerTool(Name = "list-servers")]
    [Description("List the configured SSH servers reachable through the gateway.")]
    public static JsonObject ListServers(ServerRegistry registry)
    {
        var arr = new JsonArray();
        foreach (var e in registry.All)
        {
            arr.Add(new JsonObject
            {
                ["name"] = e.Name,
                ["host"] = e.Host,
                ["port"] = e.Port,
                ["username"] = e.Username,
                ["readonly"] = !new CommandWhitelist(e.Whitelist).AllowAll,
            });
        }
        return new JsonObject { ["servers"] = arr, ["count"] = arr.Count };
    }

    [McpServerTool(Name = "execute-command")]
    [Description("Run a (read-only, whitelisted) command on a configured server and return its output.")]
    public static async Task<JsonObject> ExecuteCommand(
        ServerRegistry registry, SshClient ssh,
        [Description("Server name from list-servers")] string connectionName,
        [Description("Command to execute (must match the server's read-only whitelist)")] string cmdString,
        CancellationToken ct = default)
    {
        // Fail-fast input validation BEFORE the whitelist bound and any SSH I/O.
        if (SshgwValidation.ValidateConnectionName(connectionName) is { } nameErr)
            return Err(nameErr);
        if (SshgwValidation.ValidateCommand(cmdString) is { } cmdErr)
            return Err(cmdErr);

        var entry = registry.Get(connectionName);
        if (entry is null) return Err($"unknown server '{connectionName}'");

        // The in-MCP bound: the command must be on this server's whitelist.
        var wl = new CommandWhitelist(entry.Whitelist);
        if (!wl.IsAllowed(cmdString))
            return Err("Command not in whitelist, execution forbidden");

        var r = await ssh.ExecuteAsync(entry, cmdString, ct);
        // Compute the success verdict on the RAW exit code FIRST, so that
        // sanitizing the surfaced stderr can never flip ok/exitCode. Only the
        // human-facing stderr text is routed through the secret scrubber.
        return new JsonObject
        {
            ["ok"] = r.ExitCode == 0,
            ["exitCode"] = r.ExitCode,
            ["stdout"] = r.Stdout,
            ["stderr"] = SshgwErrors.Sanitize(r.Stderr),
        };
    }

    [McpServerTool(Name = "read_file")]
    [Description("Read a remote file's contents and return the bytes to the caller (UTF-8, or base64 if binary). Path-bounded: secret files are refused.")]
    public static async Task<JsonObject> ReadFile(
        ServerRegistry registry, SshClient ssh, SshgwOptions opts,
        [Description("Server name from list-servers")] string connectionName,
        [Description("Absolute path to the remote file")] string remotePath,
        [Description("Max bytes to return (capped at the server hard limit)")] int? max_bytes = null,
        CancellationToken ct = default)
    {
        // Fail-fast input validation BEFORE the path policy bound and any SSH I/O.
        if (SshgwValidation.ValidateConnectionName(connectionName) is { } nameErr)
            return Err(nameErr);
        if (SshgwValidation.ValidatePath(remotePath, "remotePath") is { } pathErr)
            return Err(pathErr);

        var entry = registry.Get(connectionName);
        if (entry is null) return Err($"unknown server '{connectionName}'");

        // The in-MCP bound: refuse secret paths.
        var policy = new ReadFilePolicy(entry.ReadFilePolicy);
        var reject = policy.Evaluate(remotePath, out var canonical);
        if (reject is not null) return Err(reject);

        int cap = Math.Clamp(max_bytes ?? opts.ReadFileDefaultMaxBytes, 1, opts.ReadFileHardMaxBytes);

        var res = await ssh.ReadFileAsync(entry, canonical, cap, ct);
        if (res.NotFound) return Err($"file not found: {canonical}");
        if (res.IsDirectory) return Err($"path is a directory: {canonical}");

        bool isUtf8 = TryDecodeUtf8(res.Bytes, out var text);
        return new JsonObject
        {
            ["ok"] = true,
            ["path"] = canonical,
            ["size"] = res.TotalSize,
            ["returned_bytes"] = res.Bytes.Length,
            ["truncated"] = res.Truncated,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(res.Bytes)).ToLowerInvariant(),
            ["encoding"] = isUtf8 ? "utf-8" : "base64",
            ["content"] = isUtf8 ? text : Convert.ToBase64String(res.Bytes),
        };
    }

    [McpServerTool(Name = "upload")]
    [Description("Upload a local file to a remote path (write).")]
    public static JsonObject Upload(
        [Description("Server name")] string connectionName,
        [Description("Local source path")] string localPath,
        [Description("Remote destination path")] string remotePath)
    {
        // Fail-fast input validation runs even for the stub, so the guard contract
        // (validate at the TOP, before any I/O) holds uniformly across all four
        // tools and is already in place when the SFTP body is ported.
        if (SshgwValidation.ValidateConnectionName(connectionName) is { } nameErr)
            return Err(nameErr);
        if (SshgwValidation.ValidatePath(localPath, "localPath") is { } localErr)
            return Err(localErr);
        if (SshgwValidation.ValidatePath(remotePath, "remotePath") is { } remoteErr)
            return Err(remoteErr);

        // TODO: port the SFTP upload (SftpClient.UploadFile). Left as a deliberate
        // stub so it is not silently half-implemented; this is a write tool.
        return Err("upload not yet implemented (scaffold)");
    }

    private static JsonObject Err(string message) => new() { ["ok"] = false, ["error"] = message };

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException) { text = ""; return false; }
    }
}
