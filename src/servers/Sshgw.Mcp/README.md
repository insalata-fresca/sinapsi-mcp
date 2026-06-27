# Sshgw.Mcp

A personal-lab MCP **server** that fronts a small set of SSH hosts. It is a thin
host over an SSH transport: it reads a JSON registry of servers (host + per-server
command whitelist + optional read_file path policy) and exposes a config-driven
tool surface over streamable HTTP at `/mcp`.

It is config-driven by design: the server only reaches the hosts the registry
names, and on each host only the commands and file paths the registry permits.

## Tool surface

| Tool | Verb | Bound |
|---|---|---|
| `list-servers` | read | — |
| `execute-command` | read | per-server command whitelist (`CommandWhitelist`) |
| `read_file` | read | per-server `ReadFilePolicy` (secret denylist / optional allowlist) + `max_bytes` |
| `upload` | write | (scaffold stub) |

`read_file` returns the file's bytes in the response (UTF-8, or base64 when
binary), capped by `max_bytes`, and refuses secret paths. Because the read returns
bytes, the path policy is the in-MCP bound that makes it safe — the same role the
command whitelist plays for `execute-command`.

## Configuration

| Env var | Required | Default | Purpose |
|---|:--:|---|---|
| `SSHGW_CONFIG_FILE` | no | `/etc/sshgw/servers.json` | Path to the server registry JSON (hosts + per-server whitelist + read_file policy). Mount it read-only. |
| `SSHGW_CONNECT_TIMEOUT_MS` | no | `10000` | SSH connect timeout. |
| `SSHGW_COMMAND_TIMEOUT_MS` | no | `30000` | Per-command timeout. |
| `SSHGW_READFILE_DEFAULT_MAX_BYTES` | no | `262144` | Default `read_file` cap (256 KiB). |
| `SSHGW_READFILE_HARD_MAX_BYTES` | no | `2097152` | Hard `read_file` ceiling (2 MiB). |
| `SSHGW_MCP_PORT` | no | `9204` | Listen port. |
| `SSHGW_MCP_HOST` | no | `0.0.0.0` | Listen address. |

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
    "readFilePolicy": { "allow": ["/var/log/**"] }
  }
]
```

- `whitelist` — patterns joined by `|`, each compiled as its own self-anchored
  regex. A command is allowed iff any pattern matches. Empty/absent = allow-all
  (give a read-only server an explicit whitelist instead).
- `readFilePolicy.allow` present ⇒ deny-by-default allowlist mode. Absent ⇒
  denylist mode (the global secret denylist applies, plus any extra `deny` globs).

## Run

```sh
dotnet run -c Release --project src/servers/Sshgw.Mcp
# → MCP endpoint on http://0.0.0.0:9204/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is
stripped so it cannot 400 an otherwise-valid request.
