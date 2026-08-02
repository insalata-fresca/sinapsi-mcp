using static Sshgw.Mcp.CommandCapabilities;

namespace Sshgw.Mcp;

/// <summary>
/// Pipeline-aware command authorizer — the SOLE execute-command
/// authorizer (the legacy whole-string-regex CommandWhitelist was
/// retired once this capability model was verified live).
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

    /// <summary>
    /// The REVERSIBILITY TIER of the classified command.
    /// It is ORTHOGONAL to <see cref="AuthDecision"/> and CHANGES NO decision — it only
    /// enriches the emitted Q2 verdict so a later, operator-gated flip can decide which
    /// writes are safe to auto-run in a headless session:
    /// <list type="bullet">
    ///   <item><see cref="Read"/> — an allowed read (no mutation).</item>
    ///   <item><see cref="WriteReversible"/> — a mutating command (still → RequiresApproval)
    ///     that is additive / resume-with-inverse and low-blast.</item>
    ///   <item><see cref="WriteIrreversible"/> — a mutating command (still → RequiresApproval)
    ///     that deletes/overwrites/cuts-network/execs/publishes, or whose inverse is unknown
    ///     (the fail-safe default for any ambiguous write).</item>
    ///   <item><see cref="Unknown"/> — a DENY: the command never executes, so its reversibility
    ///     is not evaluated (secret path, unknown verb, structural violation, unparseable).</item>
    /// </list>
    /// </summary>
    public enum ReversibilityTier { Read, WriteReversible, WriteIrreversible, Unknown }

    /// <summary>WHY a deny was issued. Not cosmetic: it is what lets the shadow posture relax
    /// command-CLASSIFICATION denials (an unmodelled verb, a structural violation) while keeping
    /// <see cref="DenyKind.SecretPath"/> hard.
    ///
    /// <para>A classification deny is reversible — the command runs, we record that we would have
    /// stopped it, and nothing is lost. A secret-path deny is NOT: shadowing it means the secret is
    /// read and returned into agent context, and no later decision can un-disclose it. Confidentiality
    /// is a boundary, not friction, so it is never shadowed. Typed rather than string-matched on
    /// Reason, because a reworded message must not silently disarm the carve-out.</para></summary>
    public enum DenyKind { None = 0, SecretPath, Classification }

    public sealed record AuthResult(AuthDecision Decision, string? Reason, ReversibilityTier Tier = ReversibilityTier.Read, DenyKind Deny = DenyKind.None)
    {
        public static readonly AuthResult Allowed = new(AuthDecision.Allow, null, ReversibilityTier.Read);
        public static AuthResult Approval(string reason, ReversibilityTier tier) =>
            new(AuthDecision.RequiresApproval, reason, tier);
        public static AuthResult Denied(string reason) =>
            new(AuthDecision.Deny, reason, ReversibilityTier.Unknown, DenyKind.Classification);
        /// <summary>A confidentiality deny (ReadFilePolicy secret path) — NEVER shadowed.</summary>
        public static AuthResult DeniedSecret(string reason) =>
            new(AuthDecision.Deny, reason, ReversibilityTier.Unknown, DenyKind.SecretPath);
    }

    /// <summary>The wire label for a tier, carried on the Q2 verdict envelope
    /// (<c>security.authz.q2.*</c> → <c>data.tier</c>).</summary>
    public static string TierLabel(ReversibilityTier tier) => tier switch
    {
        ReversibilityTier.Read => "read",
        ReversibilityTier.WriteReversible => "write-reversible",
        ReversibilityTier.WriteIrreversible => "write-irreversible",
        _ => "unknown",
    };

    // Map the CommandCapabilities reversibility to the public tier enum.
    private static ReversibilityTier ToTier(CommandCapabilities.Reversibility r) =>
        r == CommandCapabilities.Reversibility.Reversible
            ? ReversibilityTier.WriteReversible : ReversibilityTier.WriteIrreversible;

    public AuthResult Authorize(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return AuthResult.Denied("empty command");

        if (Lex(command, out var segments) is { } lexErr)
            return AuthResult.Denied(lexErr);
        if (segments.Count == 0)
            return AuthResult.Denied("no command segments");

        // Authorise every pipeline segment; a hard Deny dominates a soft approval.
        // TIER (behaviour-neutral enrichment): the decision aggregation is UNCHANGED — a
        // Deny still wins immediately, and a RequiresApproval still supersedes Allow. The
        // only addition is that when MULTIPLE stages require approval, the pipeline's tier
        // fail-safes to the most-severe stage tier (any irreversible stage ⇒ irreversible),
        // so a piped write is never under-labelled.
        var verdict = AuthResult.Allowed;
        foreach (var seg in segments)
        {
            var r = AuthorizeSegment(seg);
            if (r.Decision == AuthDecision.Deny) return r;         // hard refusal wins immediately
            if (r.Decision == AuthDecision.RequiresApproval)
            {
                var tier = r.Tier == ReversibilityTier.WriteIrreversible
                    || (verdict.Decision == AuthDecision.RequiresApproval
                        && verdict.Tier == ReversibilityTier.WriteIrreversible)
                    ? ReversibilityTier.WriteIrreversible
                    : ReversibilityTier.WriteReversible;
                verdict = r with { Tier = tier };
            }
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
                return AuthResult.DeniedSecret($"reads a secret-policy-blocked path ('{p}')");
        }

        if (!ReadVerbs.TryGetValue(verb, out var spec))
        {
            // A recognised mutating verb → route to approval (Canon rule 8: write =
            // per-occurrence approval). Anything else → fail-closed deny.
            if (MutatingVerbs.Contains(verb))
                return AuthResult.Approval(
                    $"'{verb}' is a write/mutating command — requires operator approval",
                    ToTier(TierForPlainVerb(verb)));
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
                // Operator-designated reversible writes that auto-run WITHOUT the ASK gate
                // (approval-without-ask; see CommandCapabilities.AutoApprovedCommands). Checked
                // BEFORE the mutating gate so the tailscale init.d wedge-recovery bounce executes
                // directly rather than dead-ending on a RequiresApproval verdict nothing actuates.
                if (AutoApprovedCommands.Contains($"{verb} {sub}"))
                    return AuthResult.Allowed;
                if (spec.MutatingSubcommands.Contains(sub))
                    return AuthResult.Approval(
                        $"'{verb} {sub}' is a write/mutating operation — requires operator approval",
                        ToTier(TierForSubcommand(verb, sub)));
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
                        // Every write-flag deletes / execs / performs an external mutating
                        // request / overwrites an on-disk file — no reversible write-flag exists.
                        return AuthResult.Approval(
                            $"'{verb}' with '{f}' writes or uploads — requires operator approval",
                            ReversibilityTier.WriteIrreversible);
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
        // `headscale` is two-level: `headscale <object> <action>` (e.g. `nodes list`
        // vs `nodes delete`) — except `version`, which has no object. Resolve the
        // compound "object action" string so CommandCapabilities can distinguish a
        // read action from a mutating one on the SAME object (mirrors `ip` above).
        if (verb == "headscale")
        {
            while (i < tokens.Count && tokens[i].StartsWith('-')) i++;
            if (i >= tokens.Count) return null;
            var obj = tokens[i];
            if (obj == "version") return obj;
            var action = i + 1 < tokens.Count ? tokens[i + 1] : null;
            return action is not null ? $"{obj} {action}" : obj;
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
        if (j < s.Length && s[j] == '>') j++�X�{