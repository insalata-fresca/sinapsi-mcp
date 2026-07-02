# Metabase.Mcp

A personal-lab MCP **server** for a [Metabase](https://www.metabase.com) analytics instance.
It is a thin, hardened host over the Metabase REST API: it wires a `MetabaseClient` over a
typed `HttpClient` and exposes the full analytics surface — catalog reads, the query capability
(run native SQL / run a saved card), a generic `request` escape hatch over ANY endpoint, and
database / table / field / card / dashboard / collection / user CRUD — over streamable HTTP at
`/mcp`. Reads are free; mutating tools are flagged `Destructive` so a fronting policy plane can
gate them.

## Contents

- [Overview](#overview)
- [Tool surface (34)](#tool-surface-34)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The server holds **no instance topology in source**. The base URL and API key are supplied by
environment variables at deploy time, so the same binary points at any Metabase deployment —
never forked code.

Architecturally it is a few small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `MetabaseConfig.cs` | Bind + validate env into an immutable record; fail-closed when required config is missing and when the port / HTTP timeout is non-numeric or out of range. |
| HTTP client | `Api/MetabaseClient.cs` | Shape the `/api/` paths over the injected `HttpClient`; surface a non-2xx response as a `MetabaseApiException` carrying the real status + body. |
| Validation | `MetabaseValidation.cs` | One `Validate*` method per tool param; returns `string?` (null = ok, else a human reason); never throws. Called at the top of each tool before any HTTP call. |
| Error scrub | `MetabaseErrors.cs` | `Sanitize(string)` redacts key material / credential assignments and length-caps every surfaced upstream/error string. |
| Tool guard | `Tools/MetabaseToolGuard.cs` | Wraps every tool body: turns a thrown `MetabaseApiException` / transport / JSON error into the `{ ok:false, status, error }` envelope with the message routed through `MetabaseErrors.Sanitize`; exposes `Rejected(reason)` for the validation short-circuit. |
| Tools | `Tools/*.cs` | The 34 MCP tools. Validate input, shape the request, and run under the guard. |

## Tool surface (34)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `list_databases` | no | List the configured databases. |
| `get_database` | no | Get one database by id. |
| `get_database_metadata` | no | Full schema metadata (tables + fields) for a database. |
| `create_database` | **yes** | Connect a new database (engine + connection-details JSON). |
| `update_database` | **yes** | Patch a database from a JSON body. |
| `delete_database` | **yes** | Disconnect/delete a database (destructive). |
| `sync_database_schema` | **yes** | Trigger a schema rescan for a database. |
| `rescan_database_values` | **yes** | Trigger a re-scan of field values for a database. |
| `list_tables` | no | List all tables Metabase knows about. |
| `get_table_metadata` | no | Query metadata (fields, types) for one table. |
| `get_field` | no | Get one field by id. |
| `update_field` | **yes** | Patch field metadata from a JSON body. |
| `list_collections` | no | List collections. |
| `get_collection_items` | no | List items inside a collection (or `root`). |
| `create_collection` | **yes** | Create a collection. |
| `update_collection` | **yes** | Patch a collection from a JSON body. |
| `list_cards` | no | List saved questions (cards). |
| `get_card` | no | Get one card by id. |
| `run_native_query` | no | Run an ad-hoc native SQL query and return rows. |
| `run_card_query` | no | Run a saved card and return its result rows. |
| `create_native_card` | **yes** | Create a native-SQL card. |
| `create_card` | **yes** | Create a card from a full body JSON (MBQL or native). |
| `update_card` | **yes** | Patch a card from a JSON body (e.g. `{"archived":true}`). |
| `delete_card` | **yes** | Delete a card permanently (destructive; prefer archiving). |
| `list_dashboards` | no | List dashboards. |
| `get_dashboard` | no | Get one dashboard by id, including its dashcards. |
| `create_dashboard` | **yes** | Create an empty dashboard. |
| `update_dashboard` | **yes** | Patch a dashboard from a JSON body (incl. the card layout). |
| `delete_dashboard` | **yes** | Delete a dashboard (destructive). |
| `add_card_to_dashboard` | **yes** | Append an existing card to a dashboard at a grid position. |
| `current_user` | no | Identity of the API key this MCP authenticates as. |
| `list_users` | no | List Metabase users. |
| `search` | no | Search across Metabase entities. |
| `request` | **yes** | Escape hatch: call ANY Metabase `/api/` endpoint (method + path + body). |

`run_native_query` / `run_card_query` are the primary value of the MCP (read the data
directly); `request` reaches anything the typed tools don't cover (alerts, pulses, permissions,
settings…). Reads are flagged `ReadOnly`; mutations are flagged `Destructive`.

## Per-tool reference

All string parameters are validated at the top of the tool **before any HTTP call**; a
validation failure returns `{ ok:false, error }` (see [Error contract](#error-contract)).
Only the non-obvious parameters are called out below.

### Read tools

- **`list_databases` / `list_tables` / `list_collections` / `list_cards` / `list_dashboards` / `list_users` / `current_user`** — no params. Return the upstream JSON.
- **`get_database` / `get_database_metadata` (id) / `get_table_metadata` (tableId) / `get_field` (fieldId) / `get_card` (cardId) / `get_dashboard` (id)** — numeric id params (typed `int`/`long`; no string validation needed). Return the upstream JSON.
- **`get_collection_items`** — `id` (string, **required**) — a collection id or the literal `root`. Rejected if empty, over 128 chars, or containing control characters. URL-escaped into the path.
- **`run_native_query`** — `databaseId` (int), `sql` (string, **required**). `sql` is rejected if empty, over 100 000 chars, or containing a non-newline control character (newlines/tabs are allowed).
- **`run_card_query`** — `cardId` (int), `parametersJson` (optional JSON-array string; rejected if present-but-malformed or over 1 MB).
- **`search`** — `q` (string, **required**; rejected if empty, over 1000 chars, or control chars), `models` (optional comma-list; rejected if over 256 chars or control chars).

### Mutating tools (flagged `Destructive`)

- **`create_database`** — `name` (**required**), `engine` (**required**, e.g. `postgres`), `detailsJson` (**required** connection JSON). All three validated; `detailsJson` must be well-formed JSON.
- **`update_database` / `update_field` / `update_collection` / `update_card` / `update_dashboard`** — `id`/`fieldId` (int) + `patchJson` (**required**, well-formed JSON). Malformed JSON is rejected before the wire.
- **`delete_database` / `delete_card` / `delete_dashboard`** — numeric id only. Destructive.
- **`sync_database_schema` / `rescan_database_values`** — numeric id; trigger a background job.
- **`create_collection`** — `name` (**required**), optional `parentId` (int), optional `description` (validated; newlines allowed).
- **`create_native_card`** — `name` (**required**), `databaseId` (int), `sql` (**required**), `display` (default `table`, validated), optional `visualizationSettingsJson` (validated JSON), optional `collectionId` (int).
- **`create_card`** — `bodyJson` (**required**, well-formed JSON — the full card body).
- **`create_dashboard`** — `name` (**required**), optional `description`, optional `collectionId` (int).
- **`add_card_to_dashboard`** — numeric params only (`dashboardId`, `cardId`, grid `row`/`col`/`sizeX`/`sizeY`). Fetches the dashboard, appends one dashcard, and saves.
- **`request`** — `method` (**required**, one of `GET|POST|PUT|DELETE` — anything else rejected), `path` (**required**, must be a relative `/api/...` path — an absolute URL is rejected so the server-held API key can't be redirected at another host), `bodyJson` (optional, validated JSON).

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `METABASE_BASE_URL` | yes | — | Instance root, e.g. `https://metrics.example.com`. The `/api/` paths are appended. Server **fails to start** if unset. |
| `METABASE_API_KEY` | yes | — | A Metabase API key, held server-side, sent as the `X-API-KEY` header. Inject at deploy; **never bake it in**. Server **fails to start** if unset. |
| `METABASE_MCP_PORT` | no | `9221` | Listen port. A non-numeric or out-of-range (`1..65535`) value **fails startup** rather than being silently ignored. |
| `METABASE_HTTP_TIMEOUT_MS` | no | `30000` | Hard ceiling on a single upstream HTTP call (`HttpClient.Timeout`). Must be an integer in `1..600000` ms; a non-numeric, `<= 0`, or out-of-range value **fails startup** (rather than making every call time out instantly or throwing deep in the `HttpClient` setter). |
| `METABASE_MCP_HOST` | no | `0.0.0.0` | Listen address. |

## Run

```sh
METABASE_BASE_URL=https://metrics.example.com \
METABASE_API_KEY=<metabase api key> \
dotnet run -c Release --project src/servers/Metabase.Mcp
# → MCP endpoint on http://0.0.0.0:9221/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped
so it cannot 400 an otherwise-valid request.

## Security notes

This server can read arbitrary data and mint / mutate real Metabase objects. It is built to
fail safe:

- **Fail-closed config.** `METABASE_BASE_URL` and `METABASE_API_KEY` are required; the server
  throws on startup if either is missing, rather than running against an unintended default.
  The port and HTTP timeout throw on a non-numeric / out-of-range value naming the offending
  var, rather than silently falling back to a default.
- **No secrets in source.** The API key lives in an env var injected at deploy
  (`METABASE_API_KEY`), is held server-side, and is sent only as the `X-API-KEY` header — never
  read into a tool response and never logged.
- **No secret leakage in errors.** Every surfaced upstream string is passed through
  `MetabaseErrors.Sanitize` before it leaves the process — uniformly across all 34 tools, reads
  included, via `MetabaseToolGuard`. PEM **private-key** blocks and
  `password=/secret=/token=/api-key=/x-api-key=/Authorization:` style assignments are redacted,
  and the message is length-capped. A database password (as `create_database` carries) or the
  API key that somehow reached an upstream error body cannot reach a caller.
- **Input validation before side effects.** Every tool validates its string parameters
  (`MetabaseValidation`) **before** any HTTP call. Invalid input returns a structured error,
  never an exception, and no request is issued.
- **Escape hatch is bounded.** `request` only accepts `GET|POST|PUT|DELETE` and only a relative
  `/api/...` path — an absolute URL is rejected so a caller can't redirect the server-held API
  key at an arbitrary host.
- **Bounded upstream calls.** Every HTTP call runs under a hard `HttpClient.Timeout`
  (`METABASE_HTTP_TIMEOUT_MS`, default 30 s) so a hung upstream cannot wedge a tool call
  indefinitely.
- **Bounded input.** JSON bodies (≤ 1 MB), SQL (≤ 100 000 chars), and other free-text params
  are length-capped to avoid a large-object-heap allocation on a pathological paste.
- **Mutations are flagged.** Every mutating tool is marked `Destructive` so a fronting policy
  plane can gate it.

## Error contract

Every tool returns a JSON object. On failure it returns `{ "ok": false, "status": <int|null>,
"error": "…" }`:

- **Validation failure** → `{ ok:false, status:null, error:"<reason>" }`, returned **before any
  HTTP call**.
- **Upstream non-2xx** → `{ ok:false, status:<http status>, error:"<sanitized body>" }`.
- **Transport / malformed-JSON failure** → `{ ok:false, status:null, error:"<sanitized>" }`.

All upstream / exception error text is scrubbed of key/credential material and length-capped by
`MetabaseErrors.Sanitize` before being returned. A successful call returns its normal upstream
payload unchanged.

## Testing

```sh
dotnet test test/Metabase.Mcp.Tests
```

The suite covers the tool-surface parity guard (the full 34-tool surface + read/mutate
classification), config binding, the HTTP client's path shaping + status carry-through, and the
**hardening paths**:

- **Config fail-closed matrix** — an invalid `METABASE_MCP_PORT` / `METABASE_HTTP_TIMEOUT_MS`
  throws at startup naming the var; a valid timeout binds + clamps at the ceiling.
- **Input validation** — every `Validate*` helper's accept/reject behaviour in isolation, and
  tool-guard tests that point the client at a **fake transport which fails the test if reached**,
  proving each tool short-circuits before any HTTP call on invalid input.
- **Error-scrub contract** — no key/credential leak + length cap, both in `MetabaseErrors`'
  unit tests and **end-to-end at the tool level**: a scripted upstream failure emits a secret in
  its body and the tool is asserted to return `[redacted]`, not the raw secret (the load-bearing
  leg mirroring StepCa's `SubprocessToolErrorTests`), plus the HTTP-status carry-through leg.
