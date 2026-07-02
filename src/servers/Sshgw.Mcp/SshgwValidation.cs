namespace Sshgw.Mcp;

/// <summary>
/// Input validation for the sshgw tool surface. Every tool parameter that flows
/// into an SSH connection, a remote command, or an SFTP path is checked here
/// BEFORE any SSH I/O is performed — and, for <c>execute-command</c> /
/// <c>read_file</c>, BEFORE the <see cref="CommandWhitelist"/> /
/// <see cref="ReadFilePolicy"/> bounds are consulted. Malformed input is rejected
/// with a clear, structured reason instead of being handed to the transport
/// (which would either fail opaquely or waste a connection).
///
/// <para>
/// Note on shell safety: commands are executed via SSH.NET's
/// <c>CreateCommand</c> (the remote host's login shell decides how to interpret
/// the string) and file paths are handed to SFTP, not a shell. This validation is
/// therefore a correctness + fail-fast guard: it rejects control characters /
/// embedded NULs (which cannot appear in a legitimate command or path and would
/// desync the SSH channel), enforces sane length caps, and defensively rejects a
/// leading <c>-</c> on a path so a hostile value can never be mistaken for an
/// SFTP-tool CLI flag. It is NOT the primary command bound — the per-server
/// <see cref="CommandWhitelist"/> and <see cref="ReadFilePolicy"/> remain the
/// security boundary; this layer runs first so their input is already well-formed.
/// </para>
///
/// <para>
/// Every <c>Validate*</c> method returns <c>null</c> when the value is valid, or a
/// human-readable reason string otherwise. None of them throw.
/// </para>
/// </summary>
internal static class SshgwValidation
{
    /// <summary>Upper bound on a server (connection) name. Registry names are short
    /// identifiers; 128 is a generous cap that still refuses an obviously-bogus
    /// blob before it is used as a dictionary lookup key.</summary>
    internal const int MaxConnectionNameLength = 128;

    /// <summary>Upper bound on a single command string. A read-only diagnostic
    /// command is short; 8 KiB is far past any legitimate whitelisted command yet
    /// still refuses a pathological megabyte-long blob.</summary>
    internal const int MaxCommandLength = 8_192;

    /// <summary>Upper bound on a remote/local file path. 4096 is the common Linux
    /// PATH_MAX; we accept up to that and refuse anything longer.</summary>
    internal const int MaxPathLength = 4_096;

    /// <summary>
    /// Validate a server (connection) name for every tool that takes one. Returns
    /// <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateConnectionName(string? connectionName)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
            return "connectionName is required";
        if (connectionName.Length > MaxConnectionNameLength)
            return $"connectionName too long ({connectionName.Length} chars; max {MaxConnectionNameLength})";
        if (ContainsControlOrNewline(connectionName))
            return "connectionName contains control characters";
        return null;
    }

    /// <summary>
    /// Validate the command string for <c>execute-command</c>. The command is
    /// still gated by the per-server <see cref="CommandWhitelist"/>; this guard
    /// only rejects input that is malformed on its face (empty, over-long, or
    /// carrying control characters / an embedded NUL that would desync the SSH
    /// channel). Returns <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "cmdString is required";
        if (command.Length > MaxCommandLength)
            return $"cmdString too long ({command.Length} chars; max {MaxCommandLength})";
        if (ContainsControlOrNewline(command))
            return "cmdString contains control characters or newlines";
        return null;
    }

    /// <summary>
    /// Validate a remote/local file path for <c>read_file</c> / <c>upload</c>.
    /// The path is still gated by <see cref="ReadFilePolicy"/> (which enforces
    /// absolute-path + secret-denylist semantics); this guard rejects input that
    /// is malformed on its face — empty, over-long, control-character/newline
    /// bearing, or starting with <c>-</c> (which could be mistaken for a CLI
    /// flag). Returns <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidatePath(string? path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"{paramName} is required";
        if (path.Length > MaxPathLength)
            return $"{paramName} too long ({path.Length} chars; max {MaxPathLength})";
        if (ContainsControlOrNewline(path))
            return $"{paramName} contains control characters or newlines";
        if (path[0] == '-')
            return $"{paramName} must not start with '-'";
        return null;
    }

    /// <summary>
    /// Validate the optional <c>directory</c> (working dir) for
    /// <c>execute-command</c>. Because the tool applies it by prefixing the command
    /// with <c>cd -- &lt;dir&gt; &amp;&amp; …</c>, this value MUST NOT be able to
    /// terminate the <c>cd</c> and start a second command — so it is held to a much
    /// tighter rule than a free path: it must be ABSOLUTE and contain ZERO shell
    /// metacharacters (no whitespace, quotes, <c>;</c>, <c>&amp;</c>, <c>|</c>,
    /// <c>$</c>, <c>`</c>, <c>&lt;&gt;</c>, <c>()</c>, <c>{}</c>, <c>*?[]</c>, <c>~</c>,
    /// <c>\</c>, <c>!</c>, <c>#</c>). This is deliberately conservative: a legitimate
    /// working directory is a plain absolute path. Returns <c>null</c> when valid,
    /// otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateDirectory(string? directory)
    {
        // Optional: an absent/empty directory means "no cd prefix" and is valid.
        if (string.IsNullOrEmpty(directory))
            return null;
        if (directory.Length > MaxPathLength)
            return $"directory too long ({directory.Length} chars; max {MaxPathLength})";
        if (directory[0] != '/')
            return "directory must be an absolute path";
        foreach (var c in directory)
        {
            if (char.IsControl(c))
                return "directory contains control characters or newlines";
            if (IsShellMetacharacter(c))
                return $"directory must not contain shell metacharacters (found '{c}')";
        }
        return null;
    }

    /// <summary>Characters that could break out of the <c>cd -- &lt;dir&gt;</c> fence
    /// or trigger shell expansion. A working directory has no legitimate need for any
    /// of them.</summary>
    private static bool IsShellMetacharacter(char c) =>
        c is ' ' or '\t' or ';' or '&' or '|' or '$' or '`' or '"' or '\''
          or '<' or '>' or '(' or ')' or '{' or '}' or '[' or ']'
          or '*' or '?' or '~' or '\\' or '!' or '#' or '\n' or '\r';

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }
}
