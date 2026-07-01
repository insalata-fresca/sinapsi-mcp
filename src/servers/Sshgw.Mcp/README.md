# Sshgw.Mcp

A personal-lab MCP **server** that fronts a small set of SSH hosts. It is a thin
host over an SSH transport: it reads a JSON registry of servers (host + per-server
command whitelist + optional read_file path policy) and exposes a config-driven
4-tool surface over streamable HTTP at `/mcp`.

It is config-driven by design: the server only reaches the hosts the registry
names, and on each host only the commands and file paths the registry permits.

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-4)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The server holds **no host topology in source**. The server registry (hosts +
per-server command whitelist + per-server read_file policy) lives in a JSON file
whose path is supplied by an environment variable at deploy time, so the binary
carries no site- or deployment-specific wiring, and the SSH identity key is
mounted rather than baked in.

Architecturally it is a handful of small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `SshgwOptions.cs` | Bind + validate env into an immutable record; fail-closed on a bad numeric bound (default + hard ceiling per value). |
| Registry | `ServerRegistry.cs` | Load + index the JSON server document once at startup. |
| Command bound | `CommandWhitelist.cs` | Per-server allowlist matcher for `execute-command`. |
| Path bound | `ReadFilePolicy.cs` | Per-server secret denylist / optional allowlist for `read_file`. |
| Validation | `SshgwValidation.cs` | Per-parameter fail-fast input checks, run at the top of every tool before the bounds and before any SSH I/O. |
| Redaction | `SshgwErrors.cs` | Scrub key material / credentials out of surfaced command stderr; length-cap it. |
| Transport | `SshClient.cs` | Open a short-lived SSH/SFTP connection per call (stateless); run the command / read the file. |
| Tools | `SshgwTools.cs` | The 4 MCP tools. Validate input, enforce the bounds, scrub surfaced errors. |

## Tool surface (4)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `list-servers` | no | Enumerate the configured servers and whether each is read-only (has a whitelist). |
| `execute-command` | no | Run a whitelisted read-only command on a named server (optional working `directory` + per-call `timeout`); return exit code + stdout + (scrubbed) stderr. |
| `read_file` | no | Read a remote file's bytes (UTF-8 or base64), path-bounded by the secret denylist / allowlist (with a remote-realpath symlink re-check), capped by `max_bytes`. |
| `upload` | **yes** | Upload a local file to a remote path via SFTP (elevated write; **not** whitelist-gated). |

## Per-tool reference

### `list-servers`
- **Params:** none.
- **Returns:** `{ servers: [{ name, host, port, username, readonly }], count }`.
  `readonly` is `true` when the server carries an explicit command whitelist.
- **Errors:** never throws.

### `execute-command`
- **Params:**
  - `cmdString` (string, **required**) — the command. Rejected if empty/whitespace, longer than 8192 chars, or containing control characters / newlines. Then gated by the per-server command whitelist (matched against the **raw** command, never the `cd`-prefixed form).
  - `connectionName` (string, optional, default `"default"`) — server name from `list-servers`. Rejected if empty/whitespace, longer than 128 chars, or containing control characters.
  - `directory` (string, optional) — absolute working directory. Applied as a `cd -- <dir> && …` prefix, so it is held to a **tighter** rule than a free path: it must be absolute and contain **no** shell metacharacters (whitespace, `; & | $ \` " ' < > ( ) { } [ ] * ? ~ \ ! #`). This makes a `cd`-breakout impossible.
  - `timeout` (int, optional, ms) — per-call command timeout, clamped to `[1, hard cap]` (the `SSHGW_COMMAND_TIMEOUT_MS` ceiling). Omitted ⇒ the configured default.
- **Returns:** `{ ok, exitCode, stdout, stderr }`. `ok`/`exitCode` are computed on the **raw** exit code **before** stderr is scrubbed, so a redaction can never flip the verdict. `stderr` is routed through `SshgwErrors.Sanitize` (or `null` when empty).
- **Errors:** input-validation failures (incl. a metachar-bearing `directory`), an unknown server, a non-whitelisted command, and a host-key-pin rejection all return `{ ok: false, error }` **before / instead of** returning output.

### `read_file`
- **Params:**
  - `connectionName` (string, **required**) — validated as above.
  - `remotePath` (string, **required**) — rejected if empty/whitespace, longer than 4096 chars, containing control characters / newlines, or starting with `-`. Then gated by the per-server `ReadFilePolicy` (absolute-path + secret denylist / allowlist).
  - `max_bytes` (int, optional) — clamped to `[1, hard ceiling]`; defaults to the configured default cap.
- **Symlink re-check:** after the lexical path policy clears, the real target is resolved on the host (`readlink -f`) and the **same** policy is re-applied to it. A symlink that sits inside an allowed dir but points at a secret is refused (`symlink target blocked: …`) before any bytes are read; the returned `path` is the resolved real target.
- **Returns:** `{ ok: true, path, size, returned_bytes, truncated, sha256, encoding, content }`. `content` is the requested file's bytes verbatim (UTF-8, or base64 when binary) — it is **deliberately not scrubbed**; the path policy is the bound that decides whether the file may be disclosed at all.
- **Errors:** input-validation failures, an unknown server, a policy-blocked path (lexical **or** symlink-resolved), a not-found path, a directory path, and a host-key-pin rejection all return `{ ok: false, error }`; only a policy-cleared path reaches SFTP.

### `upload` (mutates)
- **Params:** `localPath`, `remotePath`, `connectionName` (optional, default `"default"`) — each validated (the two paths reject a leading `-`).
- **Elevated write:** upload is **not** subject to the command whitelist or the `read_file` path policy — those bound reads. Host-key pinning still applies (MITM guard on the write too).
- **Returns:** `{ ok: true, path, bytes_sent }` on success; `{ ok: false, error }` on a missing local file, an unknown server, a validation failure, or a host-key-pin rejection.

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `SSHGW_CONFIG_FILE` | no | `/etc/sshgw/servers.json` | Path to the server registry JSON (hosts + per-server whitelist + read_file policy). Mount it read-only. |
| `SSHGW_CONNECT_TIMEOUT_MS` | no | `10000` | SSH connect timeout. Integer in `1..120000`; non-numeric / `<= 0` / out-of-range **fails startup**. |
| `SSHGW_COMMAND_TIMEOUT_MS` | no | `30000` | Per-command timeout. Integer in `1..600000`; invalid values **fail startup**. |
| `SSHGW_READFILE_DEFAULT_MAX_BYTES` | no | `262144` | Default `read_file` cap (256 KiB). Integer in `1..16777216`; must not exceed the hard cap; invalid values **fail startup**. |
| `SSHGW_READFILE_HARD_MAX_BYTES` | no | `2097152` | Hard `read_file` ceiling (2 MiB). Integer in `1..67108864`; invalid values **fail startup**. |
| `SSHGW_REQUIRE_HOST_KEY_PIN` | no | `false` | Global host-key posture. When on (`1/true/yes/on`), even a server **without** a configured `hostKeyFingerprint` is refused (no trust-on-first-use anywhere). Off ⇒ per-server pins are opt-in. An unrecognised value **fails startup**. |
| `SSHGW_MCP_HOST` | no | `0.0.0.0` | Listen address. |
| `SSHGW_MCP_PORT` | no | `9204` | Listen port. |

### Server registry format

`SSHGW_CONFIG_FILE` is a JSON array of server entries. The SSH private key is
referenced by path and mounted at deploy time — never bake it into the registry or
the image.

```json
[
  {
    "name": "example",
    "host": "ssh.example.com",
    "port": 22,
    "username": "deploy",
    "privateKey": "/etc/sshgw/keys/id_ed25519",
    "whitelist": "^uptime$|^df -h$|^cat /etc/os-release$",
    "readFilePolicy": { "allow": ["/var/log/**"] },
    "hostKeyFingerprint": "SHA256:Zm9vYmFyYmF6cXV4..."
  }
]
```

- `whitelist` — patterns joined by `|`, each compiled as its own self-anchored
  regex. A command is allowed iff any pattern matches. Empty/absent = allow-all
  (give a read-only server an explicit whitelist instead).
- `readFilePolicy.allow` present ⇒ deny-by-default allowlist mode. Absent ⇒
  denylist mode (the global secret denylist applies, plus any extra `deny` globs).
- `hostKeyFingerprint` — pinned host-key fingerprint (OpenSSH SHA-256 form
  `SHA256:<base64>`, or a raw hex SHA-256; a single string or an array for a
  rotation window). Get it with `ssh-keyscan -t ed25519 <host> | ssh-keygen -lf -`.
  When present the presented host key MUST match one of them or the connection is
  refused (MITM guard). Absent ⇒ trust-on-first-use unless
  `SSHGW_REQUIRE_HOST_KEY_PIN` is on.

## Run

```sh
SSHGW_CONFIG_FILE=/etc/sshgw/servers.json \
dotnet run -c Release --project src/servers/Sshgw.Mcp
# → MCP endpoint on http://0.0.0.0:9204/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is
stripped so it cannot 400 an otherwise-valid request.

## Security notes

This server can run commands on and read files from real hosts. It is built to
fail safe:

- **Fail-closed config.** Every numeric option has a neutral default **and** a hard
  ceiling. A non-integer, `<= 0`, or above-ceiling value — or a default byte cap
  that exceeds the hard byte cap — throws on startup naming the offending env var,
  rather than silently swapping in the default (the old behaviour). A config typo
  stops startup instead of running with a footgun (a zero timeout, an unbounded
  cap).
- **Input validation before side effects.** Every tool runs `SshgwValidation` at
  the **top**, BEFORE the whitelist / denylist bounds and BEFORE any SSH I/O:
  required/non-empty, length caps, control-char/newline rejection, and a
  leading-`-` reject on paths. Invalid input returns a structured error, never an
  exception, and never opens a connection.
- **Host-key pinning (MITM guard).** SSH.NET trusts any host key by default; this
  server wires a per-server `HostKeyPolicy` into the `HostKeyReceived` event. A
  server carrying a `hostKeyFingerprint` refuses any key that does not match it (the
  handshake is aborted before authentication); `SSHGW_REQUIRE_HOST_KEY_PIN` extends
  that to refuse even unpinned servers. A rejection surfaces as a structured error
  naming the presented fingerprint — never key material.
- **Command bound.** `execute-command` is gated by the per-server
  `CommandWhitelist` (self-anchored regexes), matched against the **raw** command
  (never the `cd`-prefixed form); a non-whitelisted command is refused before any
  SSH I/O. An optional working `directory` is validated to be absolute +
  metacharacter-free so the `cd -- <dir> && …` prefix cannot be broken out of.
- **Path bound + symlink re-check.** `read_file` is gated by the per-server
  `ReadFilePolicy` — a global secret denylist (keys, `.env`, secret-manager and
  NATS-client dirs, local DBs, …) plus an optional deny-by-default allowlist. The
  lexical policy is followed by a **remote realpath re-check** (`readlink -f`): the
  real target is resolved on the host and the same policy re-applied, so a symlink
  inside an allowed dir that points at a secret is refused before any bytes are read.
- **`upload` is an elevated write.** It is deliberately **not** gated by the command
  whitelist or the read_file path policy (those bound reads); host-key pinning still
  applies. It validates its inputs, then streams the local file over SFTP.
- **No secret leakage in surfaced errors.** `execute-command`'s surfaced `stderr`
  is passed through `SshgwErrors.Sanitize`: PEM **private-key** blocks and
  `password=/token=/secret=/Authorization:` style assignments are redacted, and the
  message is length-capped. The ok/exitCode verdict is computed on the **raw** exit
  code *before* the scrub, so a redaction can never flip it.
- **Requested content is the read's payload, not an error.** `read_file`'s
  successful `content` is **not** scrubbed — the path denylist is the bound that
  decides disclosure; once a path clears the policy, its bytes are the deliberately
  requested payload and are returned verbatim (scrubbing would corrupt them).
- **Bounded I/O.** SSH connect + per-command timeouts and `read_file`'s byte cap
  bound each call. The transport is stateless — a short-lived connection per call.

## Error contract

Every tool returns a JSON object. On error it returns `{ "ok": false, "error": "…" }`.
`execute-command`'s surfaced `stderr` is scrubbed of key/credential material and
length-capped before being returned; its `ok`/`exitCode` reflect the raw upstream
exit code. `read_file`'s successful `content` is intentionally returned verbatim.

## Testing

```sh
dotnet test test/Sshgw.Mcp.Tests
```

The suite covers the tool-surface parity guard, registry parsing, the two security
bounds (`CommandWhitelist`, `ReadFilePolicy`), the fail-closed config matrix
(default + ceiling + cross-field rejection per numeric var), the per-parameter
input validation (`SshgwValidationTests` — an `InlineData` matrix using the C#
escape `\0` for NUL inputs), and the **hardening paths**:

- `SshgwToolGuardTests` injects a fake transport that **throws if reached**, proving
  every rejection path (validation, whitelist, denylist, unknown server)
  short-circuits BEFORE any SSH I/O.
- `SshgwErrorsTests` asserts the no-key/no-credential + length-cap contract for the
  stderr scrubber.
- `SshgwToolErrorTests` (load-bearing) drives a canned transport past the guards: a
  credential emitted on stderr shows `[redacted]` in the envelope; `ok`/`exitCode`
  stay on the raw exit code (a redaction cannot flip the verdict); a
  credential-shaped requested file is returned untouched; and a timeout surfaces as
  a structured error, not an unhandled throw.
- **`CommandWhitelistParityTests` — the byte-parity merge gate.** A faithful,
  SEPARATE reimplementation of the incumbent Node matcher
  (`whitelist.split("|").map(p => new RegExp(p)).some(r => r.test(cmd))`) is run as a
  differential ORACLE, and `CommandWhitelist.IsAllowed(cmd)` is asserted **equal** to
  the oracle for EVERY `(whitelist, cmd)` row across a broad corpus that exercises
  all three whitelist SHAPES (bounded-read, router-with-writes, broad-read) and an
  adversarial reject corpus (metachar breakout, leading-dash, unbounded-read on a
  bounded set, internal-`|` crash-parity). The whitelist strings are **synthetic /
  neutral** — the real homelab whitelists are proven at SHADOW time against the live
  (private) Node server, never committed here.

  **Byte-parity scope and documented intentional divergences:** the C# matcher is
  byte-identical to the Node incumbent **except** for three explicitly documented
  families, all of which make C# *stricter* (never more permissive):

  1. **Empty-piece whitelists** (leading `|`, trailing `|`, double `||`, bare `|`):
     Node's `String.split("|")` keeps empty pieces; `new RegExp("")` matches
     everything, so any whitelist with an empty piece becomes silently allow-all
     (fail-open). C#'s `Split('|', RemoveEmptyEntries)` drops empty pieces; only
     the non-empty patterns survive, so the matcher remains fail-closed. Production
     whitelists contain no empty pieces (confirmed at shadow time); this divergence
     is a deliberate security improvement. Pinned and asserted by
     `CommandWhitelistEmptyPieceDivergenceTests`.

  2. **Whitespace-only whitelist**: C# treats a whitespace-only string as allow-all
     (`IsNullOrWhiteSpace`); Node would compile the literal spaces as a pattern that
     matches almost nothing. Only affects a degenerate misconfiguration. Pinned by
     `Whitespace_only_whitelist_is_a_documented_intended_divergence`.

  3. **Internal-`|` crash-parity**: a whitelist piece containing unbalanced-paren
     alternation (e.g. `^(uptime|whoami)$`) is torn by the `|` split into invalid
     regex fragments; both C# and Node throw rather than silently mis-matching. Pinned
     by `Internal_pipe_alternation_is_crash_parity_BOTH_throw`.

  C# is **never more permissive than Node** on any of these families — that
  one-directional invariant is explicitly asserted in
  `CommandWhitelistEmptyPieceDivergenceTests.CSharp_is_never_more_permissive_than_Node_on_empty_piece_whitelists`.
- `HostKeyPolicyTests` proves the MITM-pin logic (matching/​non-matching keys, TOFU
  vs. `requirePin`, rotation via multiple pins, and a loud throw on a malformed pin).
- `SshgwParityFeatureTests` covers the `directory` cd-prefix (incl. metachar
  rejection), the per-call `timeout` pass-through, the `upload` round-trip, and the
  `read_file` symlink realpath re-check (allowed-target read + escaping-target
  refusal). `SshClientUploadTests` covers the concrete transport's local-file guard.
