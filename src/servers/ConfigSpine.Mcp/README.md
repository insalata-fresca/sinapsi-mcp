# ConfigSpine.Mcp

A **narrow, scoped** MCP server that closes the CLAUDE.md **rule 6** gap: *every CT config
mutation must emit `homelab.config.<ctid>.<entity>.<action>` on the NATS event spine*, but there was
**no scoped tool** for an agent to do it — every mutating flow fell back to a raw `nats pub`
(`docs/67-canon-sanctioned-path-gaps.md` fix #3).

It exposes exactly one tool:

## `publish_config_event`

Record a config mutation you just made on a CT.

| Input | Meaning |
|-------|---------|
| `ctid` | numeric container id, e.g. `105` |
| `entity` | config surface changed, e.g. `acl`, `cert`, `env`, `route` |
| `action` | change verb, e.g. `added`, `rotated`, `updated`, `removed` |
| `payload` | *(optional)* free-form detail string |

It composes the subject `homelab.config.<ctid>.<entity>.<action>`, **validates it is exactly inside
`homelab.config.>`** (see below), wraps `{ctid, entity, action, detail}` in a CloudEvents v1.0
envelope (identical shape + `ch.insalata-fresca.` type prefix to the reference
`playbooks/roles/npm/files/emit_config_event.py`), and publishes it over an NKey + pinned-CA TLS
connection. Returns `{ok:true, subject}` or `{ok:false, error}`.

## Why it cannot forge events outside `homelab.config.>`

Two independent layers:

1. **Subject validation (`ConfigEventValidation`).** `ctid` must be numeric; `entity`/`action` must
   each be a single subject token (no `.`, `*`, `>`, `/`, `\`, whitespace, control chars); the
   composed subject is then re-proven to be exactly `homelab.config.<ctid>.<entity>.<action>`
   (five tokens, `homelab.config` prefix, no wildcards) **before** any publish. Unit-tested in
   `ConfigSpine.Mcp.Tests`.
2. **A dedicated publish-only nkey identity.** The server runs under its own NATS identity scoped to
   `publish: ["homelab.config.>"]`, `subscribe: ["_INBOX.>"]` — **not** admin. Even a bug in layer 1
   cannot forge an event on any other subject; the bus rejects it with a Permissions Violation. This
   is the structural backstop; layer 1 is correctness + a clear error.

## Deployment (follow-on, not part of the tool PR)

- **Identity + ACL:** the publish-only identity `config-spine-config-emit` is added to
  `nats_server_users` in `ste/home-server` (`playbooks/roles/nats_server/defaults/main.yml`),
  scoped to `homelab.config.>`. The one-time nkey mint is the operator bootstrap (register-secret
  Path D, agent-free `infisical_issue_nats_nkey`), documented in that PR.
- **Env:** see `config.env.example`. `NATS_NKEY` (public key), `NATS_NKEY_SEED_PATH` (0600 seed file
  delivered by Ansible from Infisical, Path D), `NATS_TLS_CA_FILE`, `CLOUDEVENTS_TYPE_PREFIX`,
  `CLOUDEVENTS_SOURCE`.
- **Gateway registration:** wire this backend into the CT121-mcp-gateway agentgateway via the
  `add-mcp` pattern so agents can call `publish_config_event`.

Fully env-driven and fail-soft: it boots even when NATS is unreachable and returns a structured
error on a failed publish rather than crashing the backend behind the gateway's fail-closed init.
