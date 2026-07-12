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
        [Description("Command to execute (must match the server's read-only whitelist)")] string cmdString,
        [Description("Server name from list-servers. The wire contract is 'connectionName'; 'server' is accepted as an alias. connectionName wins when both are given; when both are absent this defaults to the 'default' server.")] string? connectionName = null,
        [Description("Alias for connectionName (the harness may send the selector under this name). connectionName wins when both are given.")] string? server = null,
        [Description("Optional absolute working directory to run the command in (no shell metacharacters)")] string? directory = null,
        [Description("Optional per-call timeout in ms (clamped to the server hard cap)")] int? timeout = null,
        // Injected by DI (registered service). Defaulted + nullable so the existing
        // unit call-sites that don't pass it resolve to the legacy authorizer (their
        // asserted behaviour); production always injects the real options.
        SshgwOptions? opts = null,
        CancellationToken ct = default)
    {
        // Resolve the selector from connectionName (wire) or its 'server' alias BEFORE
        // any bound or SSH I/O. This tool historically defaults to the literal
        // 'default' server when neither is supplied (which still fails closed via the
        // registry lookup if no such server exists) — never an implicit connection.
        if (ResolveSelector(connectionName, server, bothAbsentFallback: "default", out var effectiveConn) is { } selErr)
            return selErr;
        // Fail-fast input validation BEFORE the whitelist bound and any SSH I/O.
        if (SshgwValidation.ValidateCommand(cmdString) is { } cmdErr)
            return Err(cmdErr);
        // The working directory is held to a tighter rule than a free path: it must be
        // absolute + metachar-free, because it is prefixed as `cd -- <dir> && …`.
        if (SshgwValidation.ValidateDirectory(directory) is { } dirErr)
            return Err(dirErr);

        var entry = registry.Get(effectiveConn);
        if (entry is null) return Err($"unknown server '{effectiveConn}'");

        // The in-MCP bound: authorise the RAW command (never the cd-prefixed form).
        // Two selectable models (proposal 26), flag-gated + default-legacy:
        //   legacy     — the whole-string-regex whitelist (byte-identical to today).
        //   capability — the pipeline-aware authorizer: reads (incl. piped/permuted)
        //                allow; a mutating command returns a typed requiresApproval
        //                verdict (the harness ASK gate elevates it) instead of a flat
        //                dead-end; a secret-path read is denied by the shared policy.
        if ((opts?.CommandAuthorizerMode ?? SshgwOptions.LegacyAuthorizer) == SshgwOptions.CapabilityAuthorizer)
        {
            var verdict = new CommandAuthorizer(entry).Authorize(cmdString);
            if (verdict.Decision == CommandAuthorizer.AuthDecision.Deny)
                return Err(verdict.Reason ?? "command not permitted");
            if (verdict.Decision == CommandAuthorizer.AuthDecision.RequiresApproval)
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = verdict.Reason ?? "command requires operator approval",
                    ["requiresApproval"] = true,
                };
        }
        else if (!new CommandWhitelist(entry.Whitelist).IsAllowed(cmdString))
        {
            return Err("Command not in whitelist, execution forbidden");
        }

        ExecResult r;
        try
        {
            r = await ssh.ExecuteAsync(entry, cmdString, timeout, directory, ct);
        }
        catch (HostKeyRejectedException hk)
        {
            // MITM guard fired: surface a precise, non-leaky reason (no key material).
            return Err(hk.Message);
        }

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
        [Description("Server name from list-servers. The wire contract is 'connectionName'; 'server' is accepted as an alias. connectionName wins when both are given; one of the two is REQUIRED.")] string? connectionName,
        [Description("Absolute path to the remote file")] string remotePath,
        [Description("Max bytes to return (capped at the server hard limit)")] int? max_bytes = null,
        CancellationToken ct = default,
        [Description("Alias for connectionName (the harness may send the selector under this name). connectionName wins when both are given.")] string? server = null)
    {
        // Resolve the selector from connectionName (wire) or its 'server' alias BEFORE
        // any bound or SSH I/O. read_file has no literal default: with both absent it
        // fails closed with a clear "no server specified" error.
        if (ResolveSelector(connectionName, server, bothAbsentFallback: null, out var effectiveConn) is { } selErr)
            return selErr;
        // Fail-fast input validation BEFORE the path policy bound and any SSH I/O.
        if (SshgwValidation.ValidatePath(remotePath, "remotePath") is { } pathErr)
            return Err(pathErr);

        var entry = registry.Get(effectiveConn);
        if (entry is null) return Err($"unknown server '{effectiveConn}'");

        // The in-MCP bound: refuse secret paths (lexical canonicalisation only).
        var policy = new ReadFilePolicy(entry.ReadFilePolicy);
        var reject = policy.Evaluate(remotePath, out var canonical);
        if (reject is not null) return Err(reject);

        int cap = Math.Clamp(max_bytes ?? opts.ReadFileDefaultMaxBytes, 1, opts.ReadFileHardMaxBytes);

        // SYMLINK RE-CHECK: the lexical policy above cannot see a remote symlink that
        // points OUT of an allowed dir at a secret. Resolve the REAL target over SSH
        // (`readlink -f`) and re-apply the SAME policy to it, so a symlinked secret
        // is refused before any bytes are read. Only skip the re-check if the target
        // resolves to the same canonical path (no symlink in play) — a resolution
        // that differs must clear the policy again.
        string realTarget;
        try
        {
            var resolved = await ssh.ResolveRealPathAsync(entry, canonical, ct);
            realTarget = resolved ?? canonical;
        }
        catch (HostKeyRejectedException hk)
        {
            return Err(hk.Message);
        }
        if (!string.Equals(realTarget, canonical, StringComparison.Ordinal))
        {
            var realReject = policy.Evaluate(realTarget, out var realCanonical);
            if (realReject is not null)
                return Err($"symlink target blocked: {realReject}");
            // Read from the resolved real target so we return the actual bytes the
            // policy just cleared (not the symlink path).
            canonical = realCanonical;
        }

        ReadResult res;
        try
        {
            res = await ssh.ReadFileAsync(entry, canonical, cap, ct);
        }
        catch (HostKeyRejectedException hk)
        {
            return Err(hk.Message);
        }
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
    [Description("Upload a local file to a remote path (WRITE — elevated; not whitelist-gated).")]
    public static async Task<JsonObject> Upload(
        ServerRegistry registry, SshClient ssh,
        [Description("Local source path")] string localPath,
        [Description("Remote destination path")] string remotePath,
        [Description("Server name from list-servers. The wire contract is 'connectionName'; 'server' is accepted as an alias. connectionName wins when both are given; when both are absent this defaults to the 'default' server.")] string? connectionName = null,
        [Description("Alias for connectionName (the harness may send the selector under this name). connectionName wins when both are given.")] string? server = null,
        CancellationToken ct = default)
    {
        // Resolve the selector from connectionName (wire) or its 'server' alias at the
        // TOP, before any I/O — same guard contract as the read tools, and the same
        // literal-'default' both-absent fallback as execute-command (fails closed via
        // the registry lookup when no such server exists).
        if (ResolveSelector(connectionName, server, bothAbsentFallback: "default", out var effectiveConn) is { } selErr)
            return selErr;
        // Fail-fast input validation at the TOP, before any I/O — the same guard
        // contract as the read tools.
        if (SshgwValidation.ValidatePath(localPath, "localPath") is { } localErr)
            return Err(localErr);
        if (SshgwValidation.ValidatePath(remotePath, "remotePath") is { } remoteErr)
            return Err(remoteErr);

        var entry = registry.Get(effectiveConn);
        if (entry is null) return Err($"unknown server '{effectiveConn}'");

        // upload is an ELEVATED WRITE: it is deliberately NOT subject to the command
        // whitelist or the read_file path policy (those bound READS). Host-key
        // pinning still applies via the transport (MITM guard on the write too).
        UploadResult res;
        try
        {
            res = await ssh.UploadAsync(entry, localPath, remotePath, ct);
        }
        catch (HostKeyRejectedException hk)
        {
            return Err(hk.Message);
        }
        if (res.NotFound) return Err($"local file not found: {localPath}");

        return new JsonObject
        {
            ["ok"] = res.Ok,
            ["path"] = remotePath,
            ["bytes_sent"] = res.BytesSent,
        };
    }

    /// <summary>
    /// Resolve the effective server selector from the two accepted names. The wire
    /// contract is <c>connectionName</c>; the harness-facing tool advertises the
    /// selector as <c>server</c>, so both are accepted and <c>connectionName</c>
    /// wins when present. This is a pure ALIAS — it never invents a routing default.
    ///
    /// <para>Precedence, fail-closed at every step:</para>
    /// <list type="bullet">
    /// <item><b>connectionName provided but empty/whitespace</b> ⇒ an explicit
    /// invalid selector: reject via <see cref="SshgwValidation.ValidateConnectionName"/>
    /// ("connectionName is required"). An explicit blank is never silently coerced to
    /// the alias or a default.</item>
    /// <item><b>connectionName non-empty</b> ⇒ used verbatim (byte-identical to the
    /// prior single-parameter behavior; the alias is inert).</item>
    /// <item><b>connectionName absent (null) + server non-empty</b> ⇒ the alias is
    /// used (and validated the same way).</item>
    /// <item><b>connectionName absent + server absent</b> ⇒ the caller's
    /// <paramref name="bothAbsentFallback"/>: a literal registry name for the tools
    /// that historically defaulted to <c>"default"</c> (still fails closed downstream
    /// via <see cref="ServerRegistry.Get"/> ⇒ "unknown server '…'" when absent), or
    /// <c>null</c> ⇒ the clear "no server specified" error for the tools that require
    /// an explicit selector.</item>
    /// </list>
    ///
    /// <para>Returns <c>null</c> on success (with the resolved value in
    /// <paramref name="selector"/>), or a structured error object otherwise.</para>
    /// </summary>
    private static JsonObject? ResolveSelector(
        string? connectionName, string? server, string? bothAbsentFallback, out string selector)
    {
        selector = "";

        // An explicitly-supplied connectionName is authoritative — even when blank.
        // A blank explicit value is a malformed selector, NOT an invitation to fall
        // back to the alias or a default; reject it exactly as before.
        if (connectionName is not null)
        {
            if (SshgwValidation.ValidateConnectionName(connectionName) is { } cnErr)
                return Err(cnErr);
            selector = connectionName;
            return null;
        }

        // connectionName absent ⇒ the harness alias 'server' may carry the selector.
        // A supplied-but-blank alias is likewise a malformed selector.
        if (server is not null)
        {
            if (SshgwValidation.ValidateConnectionName(server) is { } svErr)
                return Err(svErr);
            selector = server;
            return null;
        }

        // Neither selector supplied ⇒ the tool's documented both-absent behavior.
        if (string.IsNullOrWhiteSpace(bothAbsentFallback))
            // No literal fallback ⇒ a clear, explicit error — never a silent default.
            return Err("no server specified: pass connectionName (or server)");

        selector = bothAbsentFallback;
        return null;
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
