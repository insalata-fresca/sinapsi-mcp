# OpenWrtForum.Mcp

A personal-lab MCP **server** that wraps a [Discourse](https://www.discourse.org/)
forum's REST API and exposes it as a small set of MCP tools. It defaults to the
public [OpenWrt community forum](https://forum.openwrt.org) but the base URL is
env-driven, so it points at any Discourse instance.

It is a thin host over [`Sinapsi.Mcp`](../../libs/Sinapsi.Mcp): it wires a single
long-lived `DiscourseClient` (cookie jar + one-shot CSRF/login) and exposes eight
tools over streamable HTTP at `/mcp`.

This is exploratory code written for my own learning. There is no product here.

## Tools

| Tool | Auth | Action |
|---|:---:|---|
| `forum_list_categories` | read | List forum categories |
| `forum_search` | read | Search topics + posts (Discourse search syntax) |
| `forum_get_topic` | read | Fetch a topic and its posts by ID |
| `forum_get_latest` | read | Latest topics, optionally by category slug |
| `forum_create_topic` | write | Create a new topic |
| `forum_create_post` | write | Reply to an existing topic |
| `forum_get_notifications` | auth | Notifications for the configured account |
| `forum_mark_notifications_read` | write | Mark all notifications read |

Read tools work with no credentials. The write/auth tools need an account
(`DISCOURSE_API_USERNAME` + `DISCOURSE_API_PASSWORD`); without them the server
runs in read-only mode.

## Configuration

All configuration is via environment variables — nothing is baked into the image.

| Env var | Default | Purpose |
|---|---|---|
| `DISCOURSE_URL` | `https://forum.openwrt.org` | Forum base URL (trailing slash stripped). Set to any Discourse instance. |
| `DISCOURSE_API_USERNAME` | (empty → read-only) | Account username for write ops. Inject at deploy; never bake it in. |
| `DISCOURSE_API_PASSWORD` | (empty → read-only) | Account password (CSRF-protected `POST /session`). Inject at deploy. |
| `DISCOURSE_MCP_HOST` | `0.0.0.0` | Listen address. |
| `DISCOURSE_MCP_PORT` | `9207` | Listen port. |

### Example (read-only, default forum)

```sh
# No credentials → read-only tools only.
dotnet run -c Release --project src/servers/OpenWrtForum.Mcp
# → MCP endpoint on http://0.0.0.0:9207/mcp
```

### Example (a different Discourse instance, with write access)

```sh
DISCOURSE_URL=https://forum.example.com
DISCOURSE_API_USERNAME=<account username>
DISCOURSE_API_PASSWORD=<account password>
```

## Notes

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header
is stripped so it cannot 400 an otherwise-valid request.
