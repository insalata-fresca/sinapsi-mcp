using static Sshgw.Mcp.CommandCapabilities;

namespace Sshgw.Mcp;

/// <summary>
/// Pipeline-aware command authorizer (proposal 26) — the capability-model
/// alternative to the whole-string-regex <see cref="CommandWhitelist"/>.
///
/// <para>
/// It authorises <c>execute-command</c> by EFFECT, not by a flat pattern match:
/// </para>
/// <list type="number">
/// <item><b>Structural gate.</b> A quote-aware lexer rejects any command carrying a
/// side-effecting shell construct — command chaining (<c>;</c> <c>&amp;&amp;</c>
/// <c>||</c> <c>&amp;</c>), a subshell/group (<c>(</c> <c>)</c> <c>{</c> <c>}</c>),
/// redirection (<c>&gt;</c> <c>&lt;</c>), or expansion (<c>$</c>, backtick) — while
/// permitting the ONE separator a read pipeline needs, <c>|</c>. Metacharacters
/// inside single quotes are literal; <c>$</c>/backtick stay dangerous inside double
/// quotes (they still expand). This is a positive structural check, not a denylist
/// to be gamed, and it closes the chaining smuggle (<c>ls; rm -rf /etc</c>) by
/// construction.</item>
/// <item><b>Segmentation.</b> Split on the unquoted <c>|</c>; EVERY segment must
/// independently authorise as a read (the legacy matcher could not even see a
/// pipe's second segment).</item>
/// <item><b>Capability check.</b> Each segment's verb must be a known read verb with
/// a read (never mutating) sub-command and none of that verb's write/secret
/// escape-hatch flags (see <see cref="CommandCapabilities"/>). Flags are NOT
/// allow-listed — a read verb is safe under arbitrary flags in any order, which is
/// what removes the permutation explosion.</item>
/// <item><b>Secret paths.</b> Every absolute-path token (verb-independent) is run
/// through the same <see cref="ReadFilePolicy"/> that guards <c>read_file</c>, so a
/// <c>cat</c>/<c>grep</c> of a secret is refused by the one shared boundary.</item>
/// </list>
///
/// <para>Verdict precedence: a hard <see cref="AuthDecision.Deny"/> (secret path,
/// unknown verb, unparseable) dominates a <see cref="AuthDecision.RequiresApproval"/>
/// (a recognised mutating verb, routed to the harness ASK gate) — the stronger
/// refusal wins. Fail-closed throughout.</para>
/// </summary>
public sealed class CommandAuthorizer
{
    private readonly ReadFilePolicy _pathPolicy;

    public CommandAuthorizer(ServerEntry entry)
        => _pathPolicy = new ReadFilePolicy(entry.ReadFilePolicy);

    /// <summary>Test/DI seam: authorise with an explicit path policy.</summary>
    public CommandAuthorizer(ReadFilePolicy pathPolicy) => _pathPolicy = pathPolicy;

    public enum AuthDecision { Allow, RequiresApproval, Deny }

    public sealed record AuthResult(AuthDecision Decision, string? Reason)
    {
        public static readonly AuthResult Allowed = new(AuthDecision.Allow, null);
        public static AuthResult Approval(string reason) => new(AuthDecision.RequiresApproval, reason);
        public static AuthResult Denied(string reason) => new(AuthDecision.Deny, reason);
    }

    public AuthResult Authorize(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return AuthResult.Denied("empty command");

        if (Lex(command, out var segments) is { } lexErr)
            return AuthResult.Denied(lexErr);
        if (segments.Count == 0)
            return AuthResult.Denied("no command segments");

        // Authorise every pipeline segment; a hard Deny dominates a soft approval.
        var verdict = AuthResult.Allowed;
        foreach (var seg in segments)
        {
            var r = AuthorizeSegment(seg);
            if (r.Decision == AuthDecision.Deny) return r;         // hard refusal wins immediately
            if (r.Decision == AuthDecision.RequiresApproval) verdict = r;
        }
        return verdict;
    }

    // ── per-segment capability check ─────────────────────────────────────────
    private AuthResult AuthorizeSegment(List<string> tokens)
    {
        if (tokens.Count == 0)
            return AuthResult.Denied("empty pipeline segment (a stray '|')");

        var verb = BaseName(tokens[0]);

        // Secret-path guard is verb-independent: any absolute-path token (or the
        // path tail of a --opt=/path token) must clear the read_file policy. Run it
        // FIRST so a secret read is DENIED even on an otherwise-mutating verb.
        foreach (var tok in tokens)
        {
            var p = AbsolutePathCandidate(tok);
            if (p is not null && _pathPolicy.Evaluate(p, out _) is not null)
                return AuthResult.Denied($"reads a secret-policy-blocked path ('{p}')");
        }

        if (!ReadVerbs.TryGetValue(verb, out var spec))
        {
            // A recognised mutating verb → route to approval (Canon rule 8: write =
            // per-occurrence approval). Anything else → fail-closed deny.
            if (MutatingVerbs.Contains(verb))
                return AuthResult.Approval($"'{verb}' is a write/mutating command — requires operator approval");
            return AuthResult.Denied($"'{verb}' is not a recognised read command");
        }

        // Sub-command verbs: resolve the sub-command (skip leading global options,
        // including git's value-taking -C/--git-dir/-c). No sub-command found (all
        // flags, e.g. `podman --version`) ⇒ a bare read.
        if (spec.HasSubcommands)
        {
            var sub = ResolveSubcommand(verb, tokens);
            if (sub is not null)
            {
                if (spec.MutatingSubcommands.Contains(sub))
                    return AuthResult.Approval($"'{verb} {sub}' is a write/mutating operation — requires operator approval");
                if (!spec.ReadSubcommands.Contains(sub))
                    return AuthResult.Denied($"'{verb} {sub}' is not a recognised read operation");
            }
        }

        // Write/secret escape-hatch flags on an otherwise-read verb (find -delete,
        // curl -X POST, wget --post-data, …). Match whole long flags (on their
        // pre-'=' head) and expanded short-flag clusters.
        if (spec.WriteFlags.Count > 0)
        {
            foreach (var tok in tokens.Skip(1))
            {
                foreach (var f in FlagForms(tok))
                {
                    if (spec.WriteFlags.Contains(f))
                        return AuthResult.Approval($"'{verb}' with '{f}' writes or uploads — requires operator approval");
                }
            }
        }

        return AuthResult.Allowed;
    }

    // ── sub-command resolution ───────────────────────────────────────────────
    private static string? ResolveSubcommand(string verb, List<string> tokens)
    {
        int i = 1;
        // `ip` is two-level: `ip <object> [action]` — `ip route` is a read, `ip route
        // add` is a write. Return the ACTION when present (so a mutating action is
        // seen), else the object (a bare read like `ip route`).
        if (verb == "ip")
        {
            while (i < tokens.Count && tokens[i].StartsWith('-')) i++;   // skip -s/-4/-6
            if (i >= tokens.Count) return null;
            var obj = tokens[i];
            var action = i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (action is not null &&
                ReadVerbs["ip"].MutatingSubcommands.Contains(action))
                return action;
            return obj;
        }
        // git carries value-taking global options BEFORE the sub-command.
        if (verb == "git")
        {
            while (i < tokens.Count)
            {
                var t = tokens[i];
                if (t is "-C" or "--git-dir" or "--work-tree" or "--namespace" or "-c")
                { i += 2; continue; }                        // option + its value
                if (t.StartsWith("--git-dir=") || t.StartsWith("--work-tree=") ||
                    t.StartsWith("--namespace=") || t.StartsWith("-c") && t.Length > 2)
                { i += 1; continue; }                        // --opt=value form
                if (t.StartsWith('-')) { i += 1; continue; } // other global flag
                break;
            }
        }
        else
        {
            while (i < tokens.Count && tokens[i].StartsWith('-')) i++;  // skip leading flags
        }
        return i < tokens.Count ? tokens[i] : null;
    }

    // ── absolute-path extraction ─────────────────────────────────────────────
    // A token is a path candidate when it (or the value after a leading --opt=) is
    // an absolute path. URLs (http://, ssh://, docker://) are hosts, not files.
    private static string? AbsolutePathCandidate(string token)
    {
        if (token.StartsWith('/')) return token;
        int eq = token.IndexOf('=');
        if (eq >= 0 && eq + 1 < token.Length && token[eq + 1] == '/')
            return token[(eq + 1)..];
        return null;
    }

    // ── flag forms for write-flag matching ───────────────────────────────────
    // Yields the comparable flag tokens for a raw argument: the long-flag head
    // (--data from --data=x), and — for a single-dash short cluster like -sO —
    // each expanded -s, -O so a bundled dangerous short flag is still caught.
    private static IEnumerable<string> FlagForms(string token)
    {
        if (!token.StartsWith('-') || token == "-" || token == "--")
            yield break;

        if (token.StartsWith("--"))
        {
            int eq = token.IndexOf('=');
            yield return eq >= 0 ? token[..eq] : token;
            yield break;
        }

        // single-dash: could be a value-flag like -X (whole) or a short cluster -sO.
        int e = token.IndexOf('=');
        var body = e >= 0 ? token[1..e] : token[1..];
        yield return token[..(e >= 0 ? e : token.Length)];      // whole form, e.g. "-X"
        if (e < 0 && body.Length > 1 && body.All(char.IsLetter))
            foreach (var c in body) yield return "-" + c;        // expanded shorts
    }

    private static string BaseName(string verb)
    {
        int slash = verb.LastIndexOf('/');
        return slash >= 0 && slash + 1 < verb.Length ? verb[(slash + 1)..] : verb;
    }

    // A fd prefix is all-digits (or empty = no explicit fd, i.e. bare '>').
    private static bool IsAllDigits(System.Text.StringBuilder sb)
    {
        for (int k = 0; k < sb.Length; k++)
            if (!char.IsDigit(sb[k])) return false;
        return true;
    }

    // Try to consume a SAFE stream-redirection at position i (the '>' — or the '&' of
    // an '&>' when <paramref name="ampPrefixed"/>). Safe forms write no file: the
    // target is either another fd (`>&1`, `2>&1`) or the null device (`>/dev/null`,
    // `2>/dev/null`, `&>/dev/null`). On success advances <paramref name="i"/> past the
    // whole redirection and returns true; on any other target (a real file) leaves i
    // untouched and returns false so the caller rejects it.
    private static bool TryConsumeRedirection(string s, ref int i, bool ampPrefixed = false)
    {
        int j = ampPrefixed ? i + 1 : i;
        if (j >= s.Length || s[j] != '>') return false;
        j++;
        if (j < s.Length && s[j] == '>') j++;                 // '>>'
        while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;

        if (j < s.Length && s[j] == '&')                      // fd dup: >&N
        {
            j++;
            int d = j;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            if (j == d) return false;                         // need at least one digit
        }
        else                                                  // must be exactly /dev/null
        {
            const string devnull = "/dev/null";
            if (j + devnull.Length > s.Length ||
                string.CompareOrdinal(s, j, devnull, 0, devnull.Length) != 0)
                return false;
            j += devnull.Length;
            if (j < s.Length && s[j] != ' ' && s[j] != '\t' && s[j] != '|')
                return false;                                 // /dev/nullX ⇒ not the null device
        }
        i = j - 1;                                            // loop i++ moves past
        return true;
    }

    private static string RedirError() =>
        "command redirects output to a file — only the safe stream forms " +
        "('2>/dev/null', '2>&1', '>/dev/null') are permitted on a read surface";

    // ── quote-aware lexer + structural gate ──────────────────────────────────
    // Returns an error reason, or null with `segments` filled (one list of tokens
    // per '|'-separated pipeline stage). Input is already control-char/newline-free
    // (SshgwValidation.ValidateCommand runs first).
    private static string? Lex(string command, out List<List<string>> segments)
    {
        segments = new List<List<string>>();
        var seg = new List<string>();
        var tok = new System.Text.StringBuilder();
        bool inTok = false;
        char quote = '\0';                    // '\0' none, '\'' single, '"' double

        void EndTok()
        {
            if (inTok) { seg.Add(tok.ToString()); tok.Clear(); inTok = false; }
        }

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];

            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; continue; }
                // Inside double quotes, expansion is still live → forbid it.
                if (quote == '"' && (c == '$' || c == '`'))
                    return $"command contains a shell expansion ('{c}') inside double quotes — not allowed on a read surface";
                tok.Append(c); inTok = true;
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c; inTok = true;          // opening quote starts/continues a token
                    break;
                case ' ':
                case '\t':
                    EndTok();
                    break;
                case '|':
                    EndTok();
                    if (seg.Count == 0)
                        return "pipeline has an empty stage (a leading or doubled '|')";
                    segments.Add(seg); seg = new List<string>();
                    break;
                // Redirection: rejected EXCEPT the safe stream-redirection idioms that
                // write no file — `N>/dev/null`, `&>/dev/null`, `N>&M` (fd suppress/dup).
                // The fd prefix (`2`) is the current all-digit token; `&>` starts here.
                case '>':
                    if (IsAllDigits(tok) && TryConsumeRedirection(command, ref i))
                    { tok.Clear(); inTok = false; break; }        // discard the whole redirection
                    return RedirError();
                case '&':
                    if (i + 1 < command.Length && command[i + 1] == '>' &&
                        TryConsumeRedirection(command, ref i, ampPrefixed: true))
                    { EndTok(); break; }
                    return $"command contains a shell control character ('&') — chaining and backgrounding are not allowed on a read surface; use a single read pipeline";
                // structural gate — all other side-effecting constructs refused.
                case ';': case '<':
                case '(': case ')': case '{': case '}':
                case '`': case '$': case '\\': case '\n': case '\r':
                    return $"command contains a shell control character ('{(c == '\n' ? "\\n" : c == '\r' ? "\\r" : c.ToString())}') — chaining, redirection, subshell, and expansion are not allowed on a read surface; use a single read pipeline";
                default:
                    tok.Append(c); inTok = true;
                    break;
            }
        }

        if (quote != '\0')
            return "command has an unterminated quote";
        // A pending safe-redirection target (e.g. the '/dev/null' of a consumed
        // redirection) is discarded above; only real tokens remain.
        EndTok();
        if (seg.Count > 0) segments.Add(seg);
        else if (segments.Count > 0)
            return "pipeline has an empty final stage (a trailing '|')";
        return null;
    }
}
