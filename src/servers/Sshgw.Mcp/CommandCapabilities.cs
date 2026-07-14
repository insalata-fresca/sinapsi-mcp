namespace Sshgw.Mcp;

/// <summary>
/// The per-verb capability model consumed by <see cref="CommandAuthorizer"/>.
///
/// <para>
/// DESIGN (proposal 26). The legacy <see cref="CommandWhitelist"/> authorised by
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

        // ── DB / misc service probes (read-only sub-commands) ──
        ["pg_isready"] = VerbSpec.Leaf(),
        ["pg_lsclusters"] = VerbSpec.Leaf(),
        ["tailscale"] = VerbSpec.Sub(
            read: new[] { "status", "ip", "netcheck", "ping", "whois", "version",
                          "debug", "dns", "lock", "licenses", "metrics", "nc", "whois" },
            mutating: new[] { "up", "down", "set", "logout", "login", "configure",
                              "switch", "cert", "file", "funnel", "serve" }),
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
