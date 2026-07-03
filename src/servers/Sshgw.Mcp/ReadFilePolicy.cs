using System.Text.RegularExpressions;

namespace Sshgw.Mcp;

/// <summary>
/// The in-MCP bound that keeps <c>read_file</c> from returning a secret file's
/// bytes. It must guarantee that a read cannot disclose a secret.
///
/// Modes:
///   - ALLOWLIST (server entry has <c>readFilePolicy.allow</c>): deny-by-default;
///     a path must match an allow glob AND not match any deny glob. Strong form.
///   - DENYLIST (no allow globs): the GLOBAL secret denylist + any per-server
///     <c>deny</c> globs are rejected; everything else is permitted. Pragmatic
///     default; covers the common secret locations.
///
/// CAVEAT: the remote path is canonicalised LEXICALLY here (resolve '.'/'..';
/// reject residual '..'). A symlink on the remote host that escapes an allowed dir
/// is NOT caught lexically — the strong mitigation is allowlist mode plus a remote
/// <c>realpath</c> re-check (a follow-up before relying on allowlist mode for an
/// untrusted host).
/// </summary>
public sealed class ReadFilePolicy
{
    // Global secret denylist — glob form. Matched case-insensitively against the
    // canonicalised absolute path. Covers the common locations of credentials,
    // keys, and local databases.
    private static readonly string[] GlobalDeny =
    {
        "**/config.env", "**/*.env",
        "**/nats-client/**", "**/*.seed", "**/*.nk", "**/*.creds",
        "**/*.key", "**/*.pem", "**/*.pfx", "**/*.p12",
        "**/id_*", "**/.ssh/**", "/root/.ssh/**",
        "**/*.sqlite", "**/*.sqlite3", "**/*.db",
        "**/secrets/**",
        "**/vaultwarden/**", "**/bitwarden/**", "**/infisical/**",
        "**/*.jwk", "**/*.jwks", "**/jwt/**",
        // MCP config/state dir (CT121-mcp-gateway) — a dedicated secret tree. Each
        // per-MCP config JSON embeds an INLINE upstream token (e.g. proxmox-mcp.json
        // carries the mcp-gateway@pve Proxmox API token), so the whole dir must be
        // opaque to the FREE read_file — including the *.json registry itself and its
        // .bak/.pre-s31 variants, the agents/ JWK dir, and *.cred credential files.
        // Deny the tree by name, extension-independent. Verified leak 2026-07-03:
        // read_file /etc/mcp-gateway/proxmox-mcp.json returned the token in plaintext.
        "**/mcp-gateway/**",
        // Inline-credential files: *.creds (plural) was already denied; *.cred
        // (singular, e.g. bw-master.cred) was not. Cover both. Agent JWK/key dirs
        // (defence-in-depth beyond the mcp-gateway tree, per in_scope "agent JWK dirs").
        "**/*.cred",
        "**/agent-keys/**", "**/agents/**",
    };

    // Explicit re-allow carve-outs that override GlobalDeny in DENYLIST mode (for
    // non-secret generated files that happen to live under an otherwise-sensitive
    // tree, e.g. generated web-server confs).
    private static readonly string[] GlobalAllowOverride =
    {
        "**/nginx/**/*.conf",
    };

    private readonly Regex[] _allow;
    private readonly Regex[] _deny;
    private readonly Regex[] _allowOverride;
    private readonly bool _allowlistMode;

    public ReadFilePolicy(ReadFilePolicyConfig? cfg)
    {
        _allowlistMode = cfg?.Allow is { Length: > 0 };
        _allow = Compile(cfg?.Allow);
        _allowOverride = Compile(GlobalAllowOverride);
        _deny = _allowlistMode
            ? Compile(cfg?.Deny)                       // allowlist: extra denies only
            : Compile(GlobalDeny.Concat(cfg?.Deny ?? Array.Empty<string>()));
    }

    /// <summary>Returns null if allowed; a reason string if blocked.</summary>
    public string? Evaluate(string remotePath, out string canonicalPath)
    {
        canonicalPath = "";
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith('/'))
            return "remotePath must be an absolute path";

        if (!TryCanonicalise(remotePath, out canonicalPath))
            return "remotePath escapes root or contains unresolved '..'";

        // Bind to a local: an `out` parameter cannot be captured by a lambda.
        var path = canonicalPath;

        if (_allowlistMode)
        {
            if (!_allow.Any(r => r.IsMatch(path)))
                return "path not in read_file allowlist for this server";
            if (_deny.Any(r => r.IsMatch(path)))
                return "path blocked by read_file deny rule";
            return null;
        }

        // Denylist mode: blocked unless an explicit override re-allows it.
        if (_deny.Any(r => r.IsMatch(path)) &&
            !_allowOverride.Any(r => r.IsMatch(path)))
            return "path blocked by read_file secret denylist";
        return null;
    }

    private static Regex[] Compile(IEnumerable<string>? globs) =>
        (globs ?? Array.Empty<string>())
            .Select(g => new Regex(GlobToRegex(g),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();

    /// <summary>Lexical canonicalisation: collapse '//', resolve '.' and '..'.
    /// Rejects (returns false) any path that pops above root.</summary>
    private static bool TryCanonicalise(string path, out string canonical)
    {
        canonical = "";
        var stack = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count == 0) return false; // escapes root
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(seg);
        }
        canonical = "/" + string.Join('/', stack);
        return true;
    }

    /// <summary>Translate a restricted glob (supports '**', '*', '?') to an anchored
    /// regex. '**' = any chars incl. '/'; '*' = any chars except '/'.</summary>
    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}
