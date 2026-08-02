namespace Sshgw.Mcp;

/// <summary>
/// The per-verb capability model consumed by <see cref="CommandAuthorizer"/>.
///
/// <para>
/// DESIGN. The legacy CommandWhitelist authorised by
/// matching the whole command string against a <c>|</c>-split regex list — a model
/// that forbids pipes AND internal alternation by construction, so piped reads and
/// flag-permutation reads are un-whitelistable. This model instead authorises by
/// <b>effect</b>: a command is a read iff every pipeline segment is a known read
/// verb (with a read — never a mutating — subcommand), carries none of that verb's
/// small set of write/secret escape-hatch flags, and reads no secret path.
/// </para>
///
/// <para>
/// KEY SIMPLIFICATION — flags are NOT allow-listed. Enumerating every legal flag
/// per verb is exactly the permutation explosion this proposal removes. A read verb
/// is safe under ARBITRARY flags EXCEPT for a small, enumerable set of escape
/// hatches that turn a read into a write or a secret disclosure (<c>find -delete</c>,
/// <c>curl -X POST</c>, <c>sed -i</c>). We enumerate only those. Everything else on a
/// read verb is permitted, in any order. Secret-file reads are caught separately and
/// verb-independently: <see cref="CommandAuthorizer"/> runs every absolute-path token
/// through <see cref="ReadFilePolicy"/>.
/// </para>
///
/// <para>Fail-closed: a verb absent from <see cref="ReadVerbs"/> and
/// <see cref="MutatingVerbs"/> is neither allowed nor treated as a write — it is an
/// unknown verb and the authorizer denies it.</para>
/// </summary>
internal static class CommandCapabilities
{
    /// <summary>Per-verb read/write shape.</summary>
    internal sealed record VerbSpec(
        // Sub-command verbs (systemctl/podman/git/ip/…): the read sub-commands.
        // Empty ⇒ the verb has no sub-command concept and is always a read.
        IReadOnlySet<string> ReadSubcommands,
        // Sub-commands whose effect is a mutation ⇒ WRITE (route to approval).
        IReadOnlySet<string> MutatingSubcommands,
        // Flags that, on this otherwise-read verb, cause a WRITE or a broad secret
        // disclosure ⇒ route to approval / deny. Matched case-sensitively as whole
        // tokens (e.g. "-delete", "-X", "--data"). A flag written as "--opt=val" is
        // matched on its "--opt" head.
        IReadOnlySet<string> WriteFlags,
        // When true, positional args are matched as sub-commands (systemctl/podman/…);
        // when false the verb takes only flags + path/host args (cat/journalctl/…).
        bool HasSubcommands)
    {
        internal static VerbSpec Leaf(params string[] writeFlags) =>
            new(Empty, Empty, ToSet(writeFlags), HasSubcommands: false);

        internal static VerbSpec Sub(string[] read, string[] mutating, params string[] writeFlags) =>
            new(ToSet(read), ToSet(mutating), ToSet(writeFlags), HasSubcommands: true);
    }

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    private static IReadOnlySet<string> ToSet(IEnumerable<string> xs) =>
        new HashSet<string>(xs, StringComparer.Ordinal);

    // ── REVERSIBILITY TIER ───────────────────────────────────────────────────
    //
    // A SECOND, ORTHOGONAL classification layered on top of the read/write decision.
    // It does NOT change what allows/blocks (that stays exactly as the decision model
    // above): it only LABELS an already-classified WRITE as reversible vs irreversible
    // so the Q2 verdict envelope can carry the tier and a later, operator-gated flip
    // can decide which writes are safe to auto-run headless. Behaviour-neutral here.
    //
    // MODEL — reversible is an explicit ALLOWLIST; everything else mutating defaults to
    // IRREVERSIBLE (fail-safe: "when genuinely ambiguous → irreversible" — err toward
    // still-needing-approval). A verb/sub-command/flag is <see cref="Reversibility.Reversible"/>
    // ONLY when it is ADDITIVE (creates something new without clobbering existing content)
    // or a SERVICE-LIFECYCLE RESUME with a clean, well-known inverse and bounded blast
    // radius (start↔stop, up↔down, snapshot, create). Deletion, state-dropping stop/kill,
    // overwrite/truncate, arbitrary-code exec, external publish/push, package management,
    // credential/identity/network/firewall/route/power/format ops, and anything whose
    // inverse or prior state is unknown → IRREVERSIBLE.
    internal enum Reversibility { Reversible, Irreversible }

    /// <summary>Plain mutating verbs (from <see cref="MutatingVerbs"/>) that are reversible.
    /// Deliberately TINY: only pure additive creates that cannot clobber existing content.
    /// mkdir (inverse rmdir; fails on an existing dir), touch (creates, or at worst bumps
    /// mtime). cp/ln/mv/mount/dd/tee/sed/env/… default to IRREVERSIBLE — cp/ln/mv can clobber
    /// with -f (a target we cannot prove absent at this layer), the rest delete/overwrite/
    /// format/cut-network/disclose. Ambiguous-→-irreversible per the fail-safe rule.</summary>
    internal static readonly IReadOnlySet<string> ReversibleVerbs = ToSet(new[]
    {
        "mkdir", "touch",
    });

    /// <summary>OPERATOR-DESIGNATED reversible writes that AUTO-RUN without the ASK gate
    /// ("approval without ask"). Keyed as "&lt;verb&gt; &lt;sub&gt;". This makes the
    /// reversibility-tier intent concrete ("the operator decides which reversible writes
    /// are safe to auto-run in a headless session") for a specific recovery command — needed
    /// because the Q3 approve→execute loop is NOT wired, so a bare RequiresApproval verdict is
    /// a dead end (no console/broker actuates it). DELIBERATELY TINY + EXPLICIT: only the
    /// idempotent tailscale init.d service bounce (/etc/init.d/tailscale restart|reload) that
    /// recovers a control-session wedge on a managed host. Everything else mutating still routes to
    /// RequiresApproval; every other verb is untouched. Reviewed + merged as an operator
    /// decision.</summary>
    internal static readonly IReadOnlySet<string> AutoApprovedCommands = ToSet(new[]
    {
        "tailscale restart", "tailscale reload",
    });

    /// <summary>Per sub-command verb: the mutating sub-commands that are REVERSIBLE. Any
    /// mutating sub-command NOT listed here is irreversible (fail-safe default). Only verbs
    /// with at least one reversible mutating sub-command appear; omitted mutating verbs
    /// (skopeo/opkg/wg/nats/ipmitool/qm-monitor/…) have every mutating sub-command
    /// irreversible.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ReversibleSubcommands =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        // systemd: start/restart/reload/enable/unmask are additive or resume-with-inverse.
        // stop/disable/mask/kill/isolate/set-*/edit/power → irreversible (state-drop / boot
        // strand / power). disable is asymmetric to enable on purpose: enabling is additive
        // (a boot symlink, trivially undone by disable); disabling can strand a live service.
        ["systemctl"] = ToSet(new[]
        {
            "start", "restart", "reload", "try-restart", "reload-or-restart",
            "daemon-reload", "reset-failed", "enable", "unmask",
        }),
        // ip: additive route/addr add + link up are reversible (inverse del/down); del/delete/
        // flush/down are network-CUTTING and set/change/replace clobber prior state → irreversible.
        ["ip"] = ToSet(new[] { "add", "append", "up" }),
        // uci: config edits are revertable (`uci revert` pre-commit; config is snapshot/backup
        // -backed) → set/add/add_list/commit/revert reversible. delete/del_list LOSE config →
        // irreversible. (KNOWN AMBIGUITY: a uci set to firewall/network could cut the admin path;
        // the Q2 layer can't see the config section — the shadow-bake reviews this before any
        // auto-run. Follows the design's explicit "uci set+commit = revertable" call.)
        ["uci"] = ToSet(new[] { "set", "add", "add_list", "commit", "revert" }),
        // container lifecycle: start/restart/unpause/create/pull/tag/load/import/play/init are
        // additive or resume-with-inverse; attach/wait/generate are effectively benign. rm/rmi/
        // exec/stop/kill/push/build/commit/cp/save/export/prune/rename/login/logout →
        // irreversible (delete / state-drop / arbitrary exec / external push / clobber).
        ["podman"] = ToSet(new[]
        {
            "start", "restart", "unpause", "create", "pull", "tag", "load", "import",
            "play", "init", "wait", "attach", "generate",
        }),
        ["docker"] = ToSet(new[]
        {
            "start", "restart", "unpause", "create", "pull", "tag", "load", "import",
        }),
        // git: add/fetch/clone/init/stash/commit are additive + reflog/inverse-recoverable and
        // local. push (external), clean/gc/prune (unrecoverable delete), reset/checkout/switch/
        // restore (discard working tree), rebase/merge/cherry-pick/am/apply (rewrite) →
        // irreversible (fail-safe; git is where force-push/history-drop live).
        ["git"] = ToSet(new[] { "add", "fetch", "clone", "init", "stash", "commit" }),
        // zfs/zpool: create/snapshot/clone/add/attach are additive; scrub/clear are benign
        // verify/reset. destroy/rollback (DISCARDS data)/remove/detach/rename/send/receive/set
        // → irreversible.
        ["zfs"] = ToSet(new[] { "create", "snapshot", "clone" }),
        ["zpool"] = ToSet(new[] { "create", "add", "attach", "scrub", "clear" }),
        ["pvesm"] = ToSet(new[] { "add" }),
        // Hypervisor guests: start/resume/suspend (resume pair), create/clone/snapshot (additive),
        // mount/unmount (pair), unlock/fstrim (benign). stop/shutdown/reboot/reset/destroy/
        // migrate/set/resize/restore/rollback/delsnapshot/exec/enter/push/pull → irreversible.
        // exec/enter are the Tier-3 god-mode arbitrary-command path → always irreversible.
        ["pct"] = ToSet(new[]
        {
            "start", "resume", "suspend", "create", "clone", "snapshot",
            "mount", "unmount", "unlock", "fstrim",
        }),
        ["qm"] = ToSet(new[]
        {
            "start", "resume", "suspend", "create", "clone", "snapshot", "unlock",
        }),
        // tailscale: up (connect, inverse down), login (additive auth), serve/funnel (togglable
        // config), restart/reload (/etc/init.d/tailscale — idempotent service bounce re-applying
        // existing config; the control-session wedge recovery). down (network-CUT), logout (drop
        // identity), set/configure/switch/cert/file → irreversible.
        ["tailscale"] = ToSet(new[] { "up", "login", "serve", "funnel", "restart", "reload" }),
        // OpenWrt init-script restart/reload re-apply EXISTING config (idempotent service
        // bounce, no config mutation) → reversible. (KNOWN AMBIGUITY: `network restart` can
        // briefly cut the admin path — but it changes no config; the deadman-switch convention
        // covers network-breaking ops separately. Reviewed in the shadow-bake.)
        ["dnsmasq"] = ToSet(new[] { "restart", "reload" }),
        ["firewall"] = ToSet(new[] { "restart", "reload" }),
        ["network"] = ToSet(new[] { "restart", "reload" }),
        // headscale (compound "object action"): register/create/rename/tag are additive or
        // relabel; routes enable is additive (inverse disable). delete/expire/move/approve-routes/
        // routes disable+delete/policy set → irreversible (revoke / network-cut / ACL blast).
        ["headscale"] = ToSet(new[]
        {
            "nodes register", "nodes rename", "nodes tag", "routes enable",
            "users create", "users rename", "apikeys create", "preauthkeys create",
        }),
    };

    /// <summary>Reversibility of a PLAIN mutating verb (no sub-command). Reversible only when
    /// explicitly allow-listed in <see cref="ReversibleVerbs"/>; else irreversible (fail-safe).
    /// The env/printenv/export/set secret-dump verbs resolve here to IRREVERSIBLE — a disclosure
    /// cannot be un-disclosed.</summary>
    internal static Reversibility TierForPlainVerb(string verb) =>
        ReversibleVerbs.Contains(verb) ? Reversibility.Reversible : Reversibility.Irreversible;

    /// <summary>Reversibility of a mutating SUB-COMMAND on a sub-command verb. Reversible only
    /// when the verb's <see cref="ReversibleSubcommands"/> entry lists it; else irreversible.</summary>
    internal static Reversibility TierForSubcommand(string verb, string sub) =>
        ReversibleSubcommands.TryGetValue(verb, out var set) && set.Contains(sub)
            ? Reversibility.Reversible : Reversibility.Irreversible;

    // NOTE: WRITE-FLAGS (find -delete/-exec, curl -X/-d/-T/-o, wget --post-data/-O, sort -o)
    // are ALWAYS irreversible — every one deletes, runs arbitrary code, performs an external
    // mutating request, or writes/overwrites an on-disk file. There is no reversible write-flag,
    // so no allowlist is needed: CommandAuthorizer tags a write-flag hit
    // Reversibility.Irreversible unconditionally.

    /// <summary>
    /// Verbs whose plain form is a mutation regardless of args (a segment starting
    /// with one of these is a WRITE ⇒ approval). Kept separate from unknown verbs so
    /// the authorizer can say "this is a write, ask" rather than "unknown, deny".
    /// </summary>
    internal static readonly IReadOnlySet<string> MutatingVerbs = ToSet(new[]
    {
        "rm", "mv", "cp", "chmod", "chown", "chgrp", "mkdir", "rmdir", "ln", "touch",
        "tee", "dd", "truncate", "install", "mkfs", "mount", "umount", "swapon", "swapoff",
        "kill", "pkill", "killall", "reboot", "shutdown", "halt", "poweroff",
        "apt", "apt-get", "dpkg", "yum", "dnf", "pip", "pip3", "npm", "pnpm", "yarn",
        "useradd", "usermod", "userdel", "groupadd", "passwd", "chpasswd",
        "iptables", "ip6tables", "nft", "ufw", "sysctl", "modprobe", "insmod", "rmmod",
        "crontab", "at", "sed", "patch",
        // secret-dumping reads that take no path arg (so ReadFilePolicy can't catch
        // them) — treat as WRITE-class "requires approval" rather than silently allow.
        "env", "printenv", "export", "set",
    });

    /// <summary>
    /// The read-verb capability table. A verb here is authorised as a read subject to
    /// its <see cref="VerbSpec"/> (read sub-commands only; no write flags) plus the
    /// verb-independent absolute-path secret check in <see cref="CommandAuthorizer"/>.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, VerbSpec> ReadVerbs =
        new Dictionary<string, VerbSpec>(StringComparer.Ordinal)
    {
        // ── content / filesystem reads (positionals are paths → ReadFilePolicy) ──
        ["cat"] = VerbSpec.Leaf(),
        ["head"] = VerbSpec.Leaf(),
        ["tail"] = VerbSpec.Leaf(),
        ["wc"] = VerbSpec.Leaf(),
        ["nl"] = VerbSpec.Leaf(),
        ["sha256sum"] = VerbSpec.Leaf(),
        ["md5sum"] = VerbSpec.Leaf(),
        ["file"] = VerbSpec.Leaf(),
        ["stat"] = VerbSpec.Leaf(),
        ["readlink"] = VerbSpec.Leaf(),
        ["realpath"] = VerbSpec.Leaf(),
        ["basename"] = VerbSpec.Leaf(),
        ["dirname"] = VerbSpec.Leaf(),
        ["ls"] = VerbSpec.Leaf(),
        ["du"] = VerbSpec.Leaf(),
        ["df"] = VerbSpec.Leaf(),
        ["tree"] = VerbSpec.Leaf(),
        ["pwd"] = VerbSpec.Leaf(),
        // grep family: a read; the write escape hatch is none, but a pattern-file
        // (-f) or explicit output is unusual — secret file args are caught by path check.
        ["grep"] = VerbSpec.Leaf(),
        ["egrep"] = VerbSpec.Leaf(),
        ["fgrep"] = VerbSpec.Leaf(),
        ["zgrep"] = VerbSpec.Leaf(),
        ["zcat"] = VerbSpec.Leaf(),
        // find: read UNLESS it mutates/executes/writes a listing to a file.
        ["find"] = VerbSpec.Leaf("-delete", "-exec", "-execdir", "-ok", "-okdir",
                                 "-fprint", "-fprintf", "-fls"),
        ["locate"] = VerbSpec.Leaf(),
        // binary/hex/compressed content reads (positional paths → ReadFilePolicy).
        ["strings"] = VerbSpec.Leaf(),
        ["xxd"] = VerbSpec.Leaf(),
        ["od"] = VerbSpec.Leaf(),
        ["hexdump"] = VerbSpec.Leaf(),
        ["zstdcat"] = VerbSpec.Leaf(),
        ["zstdgrep"] = VerbSpec.Leaf(),
        ["bzcat"] = VerbSpec.Leaf(),
        ["xzcat"] = VerbSpec.Leaf(),
        // text filters — read-only stream stages (typically in a pipe). `sort -o`/`-S` and
        // `split`-style output are the only write escape hatches; enumerate them.
        ["rg"] = VerbSpec.Leaf(),                 // ripgrep: a read grep; paths → ReadFilePolicy
        ["sort"] = VerbSpec.Leaf("-o", "--output"),
        ["uniq"] = VerbSpec.Leaf(),
        ["tr"] = VerbSpec.Leaf(),
        ["cut"] = VerbSpec.Leaf(),
        ["fold"] = VerbSpec.Leaf(),
        ["column"] = VerbSpec.Leaf(),
        ["rev"] = VerbSpec.Leaf(),
        ["comm"] = VerbSpec.Leaf(),
        ["paste"] = VerbSpec.Leaf(),
        // package + media metadata reads (apt-cache is entirely read-only; ffprobe reads
        // media metadata — unlike ffmpeg which transcodes/writes).
        ["apt-cache"] = VerbSpec.Leaf(),
        ["dpkg-query"] = VerbSpec.Leaf(),
        ["ffprobe"] = VerbSpec.Leaf(),

        // ── service / journal / process / resource diagnostics ──
        ["systemctl"] = VerbSpec.Sub(
            read: new[] { "status", "is-active", "is-enabled", "is-failed",
                          "is-system-running", "show", "cat", "list-units",
                          "list-unit-files", "list-timers", "list-jobs", "list-sockets",
                          "list-dependencies", "get-default", "show-environment" },
            mutating: new[] { "start", "stop", "restart", "reload", "try-restart",
                              "reload-or-restart", "enable", "disable", "mask", "unmask",
                              "daemon-reload", "reset-failed", "kill", "set-property",
                              "edit", "isolate", "set-default", "poweroff", "reboot",
                              "halt", "suspend", "hibernate" }),
        ["systemd-cgtop"] = VerbSpec.Leaf(),
        ["systemd-analyze"] = VerbSpec.Leaf(),
        ["journalctl"] = VerbSpec.Leaf(),
        ["ps"] = VerbSpec.Leaf(),
        ["top"] = VerbSpec.Leaf(),
        ["htop"] = VerbSpec.Leaf(),
        ["pidof"] = VerbSpec.Leaf(),
        ["pgrep"] = VerbSpec.Leaf(),
        ["free"] = VerbSpec.Leaf(),
        ["uptime"] = VerbSpec.Leaf(),
        ["vmstat"] = VerbSpec.Leaf(),
        ["iostat"] = VerbSpec.Leaf(),
        ["mpstat"] = VerbSpec.Leaf(),
        ["lsblk"] = VerbSpec.Leaf(),
        ["lscpu"] = VerbSpec.Leaf(),
        ["lsof"] = VerbSpec.Leaf(),
        ["sensors"] = VerbSpec.Leaf(),
        ["nproc"] = VerbSpec.Leaf(),
        ["uname"] = VerbSpec.Leaf(),
        ["hostname"] = VerbSpec.Leaf(),
        ["hostnamectl"] = VerbSpec.Leaf(),
        ["whoami"] = VerbSpec.Leaf(),
        ["id"] = VerbSpec.Leaf(),
        ["who"] = VerbSpec.Leaf(),
        ["w"] = VerbSpec.Leaf(),
        ["last"] = VerbSpec.Leaf(),
        ["date"] = VerbSpec.Leaf(),
        ["dmesg"] = VerbSpec.Leaf(),
        ["getent"] = VerbSpec.Leaf(),
        ["which"] = VerbSpec.Leaf(),
        ["type"] = VerbSpec.Leaf(),
        ["command"] = VerbSpec.Leaf(),
        ["echo"] = VerbSpec.Leaf(),
        ["true"] = VerbSpec.Leaf(),
        ["help"] = VerbSpec.Leaf(),

        // ── networking diagnostics ──
        ["ip"] = VerbSpec.Sub(
            read: new[] { "route", "addr", "address", "link", "neigh", "neighbour",
                          "rule", "maddr", "tunnel", "netns", "-s", "-4", "-6", "get" },
            mutating: new[] { "add", "del", "delete", "set", "change", "replace",
                              "append", "flush", "up", "down" }),
        ["ss"] = VerbSpec.Leaf(),
        ["netstat"] = VerbSpec.Leaf(),
        ["arp"] = VerbSpec.Leaf(),
        // OpenWrt UCI config store: read sub-commands only. Mirrors the legacy
        // router whitelist, which allowed both
        // reads AND a narrow set of bounded dhcp/firewall/network/wireless writes
        // directly (no approval) — capability mode does not special-case narrow
        // bounded writes the way the old per-server regex did, so `uci set/add/
        // delete/commit/revert` now route to RequiresApproval instead of the old
        // silent allow. That is a deliberate, in-scope tightening of this migration
        // (the protected wg0/mwan3/routing stays untouched either way — reads only here).
        ["uci"] = VerbSpec.Sub(
            read: new[] { "show", "get", "export" },
            mutating: new[] { "add", "set", "delete", "commit", "revert", "add_list", "del_list" }),
        // WireGuard: `wg` (bare) and `wg show [iface] [field]` are read (matches the
        // legacy whitelist exactly). `wg set` mutates the interface — the protected wg0 is
        // sacrosanct, so this stays a mutating sub-command (routes to approval, never
        // silently allowed). `genkey`/`pubkey`/`showconf`/etc. are deliberately left
        // unmodelled (fail-closed deny) — `showconf` can disclose the private key and
        // was never in the legacy read whitelist.
        ["wg"] = VerbSpec.Sub(read: new[] { "show" }, mutating: new[] { "set" }),
        // mwan3 (OpenWrt multi-wan): the legacy whitelist allowed exactly these 5 read
        // sub-commands and no mutating form, so the mutating set is left empty here
        // (an unmodelled sub-command like `restart`/`start`/`stop` fails closed to
        // Deny — more restrictive than the old regex, never less).
        ["mwan3"] = VerbSpec.Sub(
            read: new[] { "status", "interfaces", "policies", "use", "rules" },
            mutating: Array.Empty<string>()),
        // OpenWrt opkg package manager — read sub-commands only (matches the legacy
        // whitelist's list/list-installed/info/whatdepends/search set).
        ["opkg"] = VerbSpec.Sub(
            read: new[] { "list", "list-installed", "info", "whatdepends", "search" },
            mutating: new[] { "install", "remove", "update", "upgrade", "flag" }),
        // OpenWrt syslog reader / per-interface status probe — no sub-command concept.
        ["logread"] = VerbSpec.Leaf(),
        ["ifstatus"] = VerbSpec.Leaf(),
        // OpenWrt init-script basenames reached via `/etc/init.d/<name> <verb>`
        // (BaseName strips the path, so the verb key is the script name itself). Only
        // `status` was ever in the legacy read whitelist for these three; `restart`/
        // `reload` route to approval rather than the old silent allow.
        ["dnsmasq"] = VerbSpec.Sub(read: new[] { "status" }, mutating: new[] { "restart", "reload" }),
        ["firewall"] = VerbSpec.Sub(read: new[] { "status" }, mutating: new[] { "restart", "reload" }),
        ["network"] = VerbSpec.Sub(read: new[] { "status" }, mutating: new[] { "restart", "reload" }),
        ["ping"] = VerbSpec.Leaf(),
        ["ping6"] = VerbSpec.Leaf(),
        ["traceroute"] = VerbSpec.Leaf(),
        ["mtr"] = VerbSpec.Leaf(),
        ["dig"] = VerbSpec.Leaf(),
        ["host"] = VerbSpec.Leaf(),
        ["nslookup"] = VerbSpec.Leaf(),
        ["nc"] = VerbSpec.Leaf(),          // URL/port args are hosts, not files
        ["ncat"] = VerbSpec.Leaf(),
        // curl/wget: read UNLESS a write method / upload / on-disk output is present.
        ["curl"] = VerbSpec.Leaf("-X", "--request", "-d", "--data", "--data-raw",
                                 "--data-binary", "-T", "--upload-file", "-F", "--form",
                                 "-o", "--output", "-O", "--remote-name"),
        ["wget"] = VerbSpec.Leaf("--post-data", "--post-file", "--method", "-O", "--output-document"),

        // ── container runtimes (read sub-commands only) ──
        ["podman"] = VerbSpec.Sub(
            read: new[] { "ps", "images", "image", "inspect", "logs", "version", "info",
                          "port", "top", "stats", "search", "history", "diff", "exists",
                          "healthcheck", "system", "pod", "volume", "network", "manifest",
                          "container", "unshare", "mount" },
            mutating: new[] { "run", "rm", "rmi", "exec", "stop", "start", "restart",
                              "kill", "pull", "push", "build", "tag", "create", "commit",
                              "cp", "load", "save", "import", "export", "prune", "rename",
                              "pause", "unpause", "wait", "init", "attach", "generate",
                              "play", "login", "logout" }),
        ["docker"] = VerbSpec.Sub(
            read: new[] { "ps", "images", "image", "inspect", "logs", "version", "info",
                          "port", "top", "stats", "search", "history", "diff", "system",
                          "volume", "network", "container", "compose" },
            mutating: new[] { "run", "rm", "rmi", "exec", "stop", "start", "restart",
                              "kill", "pull", "push", "build", "tag", "create", "commit",
                              "cp", "load", "save", "import", "export", "prune", "rename",
                              "pause", "unpause", "login", "logout" }),
        ["skopeo"] = VerbSpec.Sub(
            read: new[] { "inspect", "list-tags", "list-repositories" },
            mutating: new[] { "copy", "delete", "sync", "login", "logout" }),

        // ── git (read sub-commands only; -C <dir>/--git-dir handled by the authorizer) ──
        ["git"] = VerbSpec.Sub(
            read: new[] { "log", "status", "show", "diff", "rev-parse", "remote",
                          "branch", "describe", "ls-files", "ls-tree", "cat-file",
                          "rev-list", "shortlog", "blame", "reflog", "for-each-ref",
                          "symbolic-ref", "name-rev", "merge-base", "show-ref",
                          "count-objects", "var", "help", "version", "tag", "config" },
            mutating: new[] { "commit", "push", "pull", "fetch", "checkout", "switch",
                              "reset", "clean", "rebase", "merge", "cherry-pick", "revert",
                              "add", "mv", "stash", "init", "clone", "apply", "am",
                              "gc", "prune", "worktree", "restore", "update-ref",
                              "update-index", "write-tree", "commit-tree", "sparse-checkout" }),

        // ── storage / virtualisation reads ──
        ["zfs"] = VerbSpec.Sub(read: new[] { "list", "get", "version" },
                               mutating: new[] { "create", "destroy", "set", "snapshot",
                                                 "rollback", "clone", "rename", "send", "receive" }),
        ["zpool"] = VerbSpec.Sub(read: new[] { "list", "status", "get", "history", "iostat" },
                                 mutating: new[] { "create", "destroy", "add", "remove",
                                                   "set", "scrub", "clear", "attach", "detach" }),
        ["pveversion"] = VerbSpec.Leaf(),
        ["pvesm"] = VerbSpec.Sub(read: new[] { "status", "list" },
                                 mutating: new[] { "add", "remove", "set" }),
        // Container/VM CLIs on the hypervisor host — read sub-commands
        // only (matches the legacy whitelist's `pct list/config <id>/status <id>/
        // listsnapshot <id>` and `qm list/config <id>/status <id>`). Every mutating
        // sub-command — including `exec`/`enter`, which is the Tier-3 god-mode path
        // when run via `pct enter`/`pct exec` directly on the hypervisor — routes to
        // RequiresApproval here rather than a silent allow; it is NOT a substitute
        // for the Tier-3 elevation gate, just the same fail-safe every other
        // mutating sub-command on this table gets.
        ["pct"] = VerbSpec.Sub(
            read: new[] { "list", "config", "status", "listsnapshot" },
            mutating: new[] { "start", "stop", "shutdown", "reboot", "suspend", "resume",
                               "destroy", "create", "clone", "migrate", "set", "resize",
                               "restore", "snapshot", "rollback", "delsnapshot", "exec",
                               "enter", "push", "pull", "unlock", "fstrim", "mount", "unmount" }),
        ["qm"] = VerbSpec.Sub(
            read: new[] { "list", "config", "status" },
            mutating: new[] { "start", "stop", "shutdown", "reset", "suspend", "resume",
                               "destroy", "create", "clone", "migrate", "set", "resize",
                               "monitor", "terminal", "agent", "unlock", "importdisk",
                               "move-disk", "snapshot", "rollback", "delsnapshot" }),

        // ── DB / misc service probes (read-only sub-commands) ──
        ["pg_isready"] = VerbSpec.Leaf(),
        ["pg_lsclusters"] = VerbSpec.Leaf(),
        ["tailscale"] = VerbSpec.Sub(
            read: new[] { "status", "ip", "netcheck", "ping", "whois", "version",
                          "debug", "dns", "lock", "licenses", "metrics", "nc", "whois" },
            mutating: new[] { "up", "down", "set", "logout", "login", "configure",
                              "switch", "cert", "file", "funnel", "serve",
                              // /etc/init.d/tailscale <action> (BaseName strips the path, so the
                              // init-script action lands here too): the sanctioned
                              // tailscale control-session-wedge recovery — now an
                              // approval-gated write, not the retired silent whitelist. restart/
                              // reload re-apply EXISTING config (idempotent bounce) → reversible.
                              "restart", "reload" }),
        // Headscale (self-hosted Tailscale control server) — two-level `<object>
        // <action>` sub-commands, resolved specially in CommandAuthorizer.ResolveSubcommand
        // (mirrors `ip`'s object+action handling) because a single-level "nodes" read
        // would otherwise also let `headscale nodes delete <id>` through. Read
        // sub-commands are the compound "object action" strings below; `version` has
        // no object, so it resolves bare. Every mutating action (create/delete/expire/
        // set/rename/move/approve-routes/register/tag/enable/disable) routes to
        // RequiresApproval, never a silent allow. Unblocks the headscale-upgrade
        // session (S-noted: headscale was previously entirely unmodelled).
        ["headscale"] = VerbSpec.Sub(
            read: new[]
            {
                "version",
                "nodes list", "routes list", "users list",
                "apikeys list", "preauthkeys list", "policy get",
            },
            mutating: new[]
            {
                "nodes delete", "nodes expire", "nodes register", "nodes rename",
                "nodes move", "nodes approve-routes", "nodes tag",
                "routes enable", "routes disable", "routes delete",
                "users create", "users rename", "users delete",
                "apikeys create", "apikeys expire", "apikeys delete",
                "preauthkeys create", "preauthkeys expire",
                "policy set",
            }),
        ["nats"] = VerbSpec.Sub(
            read: new[] { "stream", "consumer", "server", "account", "kv", "object",
                          "sub", "context", "schema" },
            mutating: new[] { "pub", "publish", "request", "reply", "bench" }),
        ["dotnet"] = VerbSpec.Leaf(),      // typically --version/--info/--list-*
        ["ipmitool"] = VerbSpec.Sub(
            read: new[] { "sel", "sensor", "sdr", "fru", "mc", "chassis", "lan", "user" },
            mutating: new[] { "power", "raw" }),
    };
}
