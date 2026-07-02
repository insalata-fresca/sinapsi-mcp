using System.Text.RegularExpressions;

namespace Sshgw.Mcp;

/// <summary>
/// Per-server command allowlist matcher.
///
/// CONTRACT:
///   - The whitelist is a single string; it is SPLIT on '|' and each piece is
///     compiled as its OWN regex (<c>new Regex(piece)</c>). Patterns are expected
///     to be self-anchored (^...$) and contain NO internal alternation. A command
///     is allowed iff ANY piece matches.
///   - An empty/whitespace whitelist = allow-all. A read-only server should always
///     carry an explicit whitelist; allow-all is only for a server that
///     intentionally has none.
///
/// This is the in-MCP bound that keeps execute-command to a known, read-only set
/// of commands, so it is enforced HERE before any SSH I/O. It is covered by a
/// dedicated unit suite (see the test project).
/// </summary>
public sealed class CommandWhitelist
{
    private readonly Regex[] _patterns;
    public bool AllowAll { get; }

    public CommandWhitelist(string? whitelist)
    {
        if (string.IsNullOrWhiteSpace(whitelist))
        {
            AllowAll = true;
            _patterns = Array.Empty<Regex>();
            return;
        }

        _patterns = whitelist
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            // Each piece is compiled verbatim. The patterns self-anchor with ^...$;
            // do NOT add anchors here (that would change the semantics for any
            // non-anchored piece).
            .Select(p => new Regex(p, RegexOptions.CultureInvariant))
            .ToArray();
    }

    public bool IsAllowed(string command)
    {
        if (AllowAll) return true;
        if (string.IsNullOrEmpty(command)) return false;
        foreach (var re in _patterns)
            if (re.IsMatch(command)) return true;
        return false;
    }
}
