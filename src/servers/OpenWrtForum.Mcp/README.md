# OpenWrtForum.Mcp

A personal-lab MCP **server** that wraps a [Discourse](https://www.discourse.org/)
forum's REST API and exposes it as a small set of MCP tools over streamable HTTP
at `/mcp`. It defaults to the public
[OpenWrt community forum](https://forum.openwrt.org) — a genuine public Discourse
instance — but the base URL is env-driven, so it points at any Discourse forum.

It is a thin, hardened host over [`Sinapsi.Mcp`](../../libs/Sinapsi.Mcp): it wires
a single long-lived `DiscourseClient` (cookie jar + one-shot CSRF/login) and
exposes an 8-tool surface. Input is validated before any HTTP call, outbound
requests are bounded by a configurable timeout, and every upstream error string is
scrubbed of key/credential material before it can reach a caller.

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-8)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The server holds **no forum topology in source**. The base URL, account username
and password are all supplied by environment variables at deploy time; the only
default is the genuine public OpenWrt forum, so the binary carries no
deployment-specific wiring. With no credentials it runs read-only.

Architecturally it is three small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `DiscourseOptions.cs` | Bind + validate env into an immutable record; fail-closed on a bad HTTP-timeout value (throws naming the var). |
| Transport | `DiscourseClient.cs` | One long-lived `HttpClient` + cookie jar, one-shot CSRF/login, a bounded per-request timeout, and a structured `{error,status_code,body}` failure envelope with the body scrubbed of secrets. |
| Tools | `ForumTools.cs` | The 8 MCP tools. Validates input (`OpenWrtForumValidation`) before any HTTP call and routes every surfaced error through `OpenWrtForumErrors.Sanitize`. |

## Tool surface (8)

| Tool | Auth | Mutates | What it does |
|------|:----:|:-------:|--------------|
| `forum_list_categories` | read | no | List forum categories (`id`, `name`, `slug`, `topic_count`). |
| `forum_search` | read | no | Search topics + posts with Discourse search syntax; projects capped, URL-resolved results. |
| `forum_get_topic` | read | no | Fetch a topic and its posts by numeric id. |
| `forum_get_latest` | read | no | Latest topics, optionally scoped to a category slug. |
| `forum_create_topic` | write | **yes** | Create a new topic (title + markdown body + category + optional tags). |
| `forum_create_post` | write | **yes** | Reply to an existing topic. |
| `forum_get_notifications` | auth | no | Notifications (replies/mentions) for the configured account. |
| `forum_mark_notifications_read` | write | **yes** | Mark all notifications read. |

Read tools work with no credentials. The write/auth tools need an account
(`DISCOURSE_API_USERNAME` + `DISCOURSE_API_PASSWORD`); without them the server runs
in read-only mode and the auth tools do not attempt a login.

## Per-tool reference

### `forum_list_categories`
- **Params:** none.
- **Returns:** a JSON array of `{ id, name, slug, topic_count }`.
- **Errors:** a non-2xx upstream throws the structured `{ error, status_code, body }`
  envelope (body scrubbed of secrets).

### `forum_search`
- **Params:**
  - `query` (string, **required**) — Discourse search syntax. Rejected if empty/whitespace, longer than 500 chars, or containing control characters.
  - `page` (int, default `1`) — must be `0..1000`.
- **Returns:** `{ topics: [...], posts: [...] }` — topics capped at 25, posts at 10, each with a resolved `url`; post excerpts truncated to 400 chars.
- **Errors:** input-validation failures return `{ ok: false, error }` **before any HTTP request**.

### `forum_get_topic`
- **Params:**
  - `topic_id` (int, **required**) — must be a positive integer.
  - `page` (int, default `0`) — must be `0..1000`.
- **Returns:** `{ id, title, url, created_at, reply_count, views, category_id, tags, posts }`; post bodies truncated to 3000 chars.
- **Errors:** validation failures short-circuit to `{ ok: false, error }` before any HTTP request.

### `forum_get_latest`
- **Params:**
  - `category_slug` (string, optional) — a URL path segment. Rejected if longer than 128 chars, containing control characters, starting with `-`, or containing a path separator (`/`, `\`). Absent → the site-wide latest feed.
  - `page` (int, default `0`) — must be `0..1000`.
- **Returns:** a JSON array of latest topics (capped at 30) with resolved URLs.
- **Errors:** validation failures short-circuit to `{ ok: false, error }` before any HTTP request.

### `forum_create_topic` (mutates)
- **Params:**
  - `title` (string, **required**) — rejected if empty/whitespace, longer than 300 chars, or containing control characters.
  - `body` (string, **required**) — markdown; newlines/tabs allowed, but a NUL or other C0 control is rejected; capped at 64 KiB.
  - `category_id` (int, **required**) — must be a positive integer.
  - `tags` (string[], optional) — at most 30; each non-empty, ≤ 100 chars, no control characters.
- **Returns:** `{ topic_id, post_id, url, status: "created" }`.
- **Errors:** every parameter is validated **before the login handshake or any POST**, so a malformed create never authenticates or mutates the forum. A validation failure returns `{ ok: false, error }`; an upstream failure throws the scrubbed structured envelope.

### `forum_create_post` (mutates)
- **Params:**
  - `topic_id` (int, **required**) — must be a positive integer.
  - `body` (string, **required**) — validated like `forum_create_topic`'s body.
- **Returns:** `{ post_id, topic_id, post_number, url, status: "created" }`.
- **Errors:** validation failures short-circuit to `{ ok: false, error }` before any HTTP request.

### `forum_get_notifications`
- **Params:** `filter` (string, default `"all"`) — must be exactly `"all"` or `"unread"`.
- **Returns:** a JSON array of `{ id, type, read, created_at, topic_id, topic_title, excerpt, url }`; a notification with no topic has `topic_id`/`url` null.
- **Errors:** an unknown `filter` returns `{ ok: false, error }` before any HTTP request. In read-only mode no login is attempted.

### `forum_mark_notifications_read` (mutates)
- **Params:** none.
- **Returns:** `{ status: "all notifications marked read" }`.
- **Errors:** an upstream failure throws the scrubbed structured envelope.

## Configuration

All configuration is via environment variables — nothing is baked into the image.

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `DISCOURSE_URL` | no | `https://forum.openwrt.org` | Forum base URL (trailing slash stripped). The default is the genuine public OpenWrt forum; set to any Discourse instance. |
| `DISCOURSE_API_USERNAME` | no | (empty → read-only) | Account username for write ops. Inject at deploy; never bake it in. |
| `DISCOURSE_API_PASSWORD` | no | (empty → read-only) | Account password (CSRF-protected `POST /session`). Inject at deploy; never logged or returned. |
| `DISCOURSE_HTTP_TIMEOUT_MS` | no | `30000` | Hard ceiling on every outbound Discourse HTTP call. Must be an integer in `1..600000` ms; a non-numeric, `<= 0`, or out-of-range value **fails startup** (rather than silently making every call a footgun or crashing deep in the HTTP path). |
| `DISCOURSE_MCP_HOST` | no | `0.0.0.0` | Listen address. |
| `DISCOURSE_MCP_PORT` | no | `9207` | Listen port. |

## Run

```sh
# No credentials → read-only tools only, against the default public forum.
dotnet run -c Release --project src/servers/OpenWrtForum.Mcp
# → MCP endpoint on http://0.0.0.0:9207/mcp
```

```sh
# A different Discourse instance, with write access.
DISCOURSE_URL=https://forum.example.com \
DISCOURSE_API_USERNAME=<account username> \
DISCOURSE_API_PASSWORD=<account password> \
dotnet run -c Release --project src/servers/OpenWrtForum.Mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is
stripped so it cannot 400 an otherwise-valid request.

## Security notes

This server can create topics and posts on a real forum with a real account. It is
built to fail safe:

- **Fail-closed config.** A malformed `DISCOURSE_HTTP_TIMEOUT_MS` (non-numeric,
  `<= 0`, or above the 10-minute ceiling) throws on startup naming the offending
  env var, rather than silently honouring a footgun value.
- **No secrets in source.** The account password lives in `DISCOURSE_API_PASSWORD`,
  injected at deploy. It is never read into a tool response and never logged.
- **No secret leakage in errors.** Every surfaced upstream string is passed through
  `OpenWrtForumErrors.Sanitize` before it leaves the process — uniformly across all
  eight tools, reads included. The failure envelope's `body` is scrubbed at the
  point it is built in `DiscourseClient`, so a verbose forum error that echoed the
  account password, a session token, or a pasted key cannot reach a caller. PEM
  **private-key** blocks and `password=/token=/secret=/api-key=/Authorization:`
  style assignments are redacted, and the message is length-capped.
- **Input validation before side effects.** Every tool validates its parameters
  (`OpenWrtForumValidation`) **before** any HTTP call. For the mutating create
  tools this runs **before the login handshake**, so a malformed create never
  authenticates or mutates the forum. Invalid input returns a structured
  `{ ok: false, error }`, never an exception.
- **Path-segment safety.** `category_slug` reaches a URL path segment; beyond
  length + control-char checks it rejects a leading `-` and any path separator so
  it cannot traverse or masquerade as another path component. Query-string values
  are percent-encoded and body values are JSON-serialised.
- **Bounded HTTP.** Every outbound call runs under `HttpClient.Timeout`
  (`DISCOURSE_HTTP_TIMEOUT_MS`, default 30 s), so a hung/slow forum cannot wedge a
  request.
- **Bounded input.** A topic/post body is capped at 64 KiB and a search query at
  500 chars to avoid a large-allocation on a pathological paste.

## Error contract

Every tool returns a JSON string. On an **input-validation** failure it returns
`{ "ok": false, "error": "…" }` **before any HTTP request is made**. On an
**upstream** failure the underlying `DiscourseClient` throws a structured
`{ "error": "discourse <status>", "status_code": <n>, "body": <scrubbed> }`
envelope; the `body` has already been scrubbed of any key/credential material and
length-capped, so no secret in an upstream response can reach a caller.

## Testing

```sh
dotnet test test/OpenWrtForum.Mcp.Tests
```

The suite drives the whole surface through a stub `HttpMessageHandler` (no network,
no live forum) and covers: the read/write tool shapes; config binding (defaults +
override + the fail-closed HTTP-timeout matrix, naming the offending var); the
input-validation matrix for every tool parameter (using the C# `\0` escape for NUL
inputs, never a literal NUL byte); and the **hardening paths** —

- **Short-circuit:** each tool is driven with a malformed parameter through a
  transport that **throws if it is ever reached**, proving validation rejects the
  input with `{ ok: false, error }` *before* any HTTP call (a valid-input control
  test confirms the guard is not always-on).
- **Error scrub end-to-end:** a fake transport emits a secret (a
  `password=` assignment and a PEM key block) in an upstream error body; the
  surfaced error contains `[redacted]`, **not** the raw secret.
- **Timeout:** a hanging transport is torn down by the configured
  `HttpClient.Timeout` rather than left to hang.
