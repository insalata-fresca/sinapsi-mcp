# Gdrive.Mcp

A self-hosted **Google Drive CRUD MCP server**. Many managed Drive connectors expose only
read/search/download/create — they have **no `update` and no `delete`**, and a third-party
connector can't be extended. This server owns the full file lifecycle, talking to Drive via
the official [`Google.Apis.Drive.v3`](https://www.nuget.org/packages/Google.Apis.Drive.v3)
.NET client and exposing a 9-tool surface over streamable HTTP at `/mcp`.

It holds **no deployment topology in source** — credential paths, the listen address, the
externally-reachable download base URL, and every timeout are supplied by environment
variables at deploy time (defaults are neutral local placeholders). The hardening bar —
per-parameter input validation, fail-closed config, uniform error sanitisation — mirrors the
ratified `StepCa.Mcp` exemplar, adapted for an HTTP-backed (rather than subprocess) server.

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-9)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Auth — one-time setup](#auth--one-time-setup)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Downloading binaries / large files](#downloading-binaries--large-files)
- [Testing](#testing)

## Overview

The server holds **no Drive topology in source**. The OAuth client + refresh-token paths, the
application name, the download base URL, the ticket TTL, and the per-request HTTP timeout are
all supplied by environment variables at deploy time, so the binary carries no site- or
deployment-specific wiring.

Architecturally it is a handful of small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `GdriveConfig.cs` | Bind + validate env into an immutable record; fail-closed (throws, naming the var) on a bad HTTP-timeout value. |
| Auth | `DriveClientFactory.cs` | Build one authenticated `DriveService` from a stored refresh token; clamp `HttpClient.Timeout`; fail fast at startup if the token is absent/invalid. |
| Validation | `GdriveValidation.cs` | One throw-free `Validate*` method per tool parameter; returns `null` or a human-readable reason. |
| Errors | `GdriveErrors.cs` | `Sanitize(...)` — redacts key material / OAuth tokens / credentials and length-caps every surfaced error string. |
| Download | `DriveDownload.cs` | Ranged base64 media plumbing + the short-lived capability-ticket store + the `GET /gdrive-dl/{token}` streaming endpoint. |
| Tools | `DriveTools.cs` | The 9 MCP tools. Validates input first, calls the Drive client, routes every error string through `Sanitize`. |

## Tool surface (9)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `list_files` | no | List files (newest first); optional parent folder + trashed toggle. |
| `search_files` | no | Raw Drive `q` query. |
| `get_file_metadata` | no | Full metadata for one file id. |
| `download_file` | no | Download content as UTF-8 **text** (size-capped; lossy for binaries). |
| `download_file_base64` | no | **Lossless** binary download as **base64 over an HTTP byte range** — any type, any size. |
| `download_to_url` | no | **Best for big files:** stage a server-side stream + return a short-lived internal `wget` URL. |
| `create_file` | **yes** | Create a file with text content, optional parent folder. |
| `update_file` | **yes** | **Rename and/or replace content** of an existing file. |
| `delete_file` | **yes** | **Trash (default) or permanently delete** a file. |

`update_file` + `delete_file` are the two a typical managed connector lacks and the reason
this server exists.

## Per-tool reference

Every tool validates its parameters first and, on bad input or an upstream failure, returns
the uniform `{ ok: false, error }` envelope (see [Error contract](#error-contract)). The
success shapes below are unchanged from the tool's behaviour.

### `list_files`
- **Params:** `folderId?` (optional parent), `pageSize` (1–1000, clamped, default 50), `includeTrashed` (default false).
- **Returns:** `{ count, files[], nextPageToken }` where each file is `{ id, name, mimeType, size, modifiedTime, createdTime, parents, trashed, webViewLink, owners }`.
- **Errors:** invalid `folderId` (whitespace/control-char/leading-`-`/over-length) → `{ ok:false, error }`; an upstream failure → the same envelope with a scrubbed message.

### `search_files`
- **Params:** `query` (required, raw Drive `q` expression), `pageSize` (1–1000, clamped, default 50).
- **Returns:** `{ count, files[], nextPageToken }`.
- **Errors:** empty / over-length / control-char `query` → `{ ok:false, error }`.

### `get_file_metadata`
- **Params:** `fileId` (required).
- **Returns:** the file summary object (as in `list_files`).
- **Errors:** invalid `fileId` → `{ ok:false, error }`.

### `download_file`
- **Params:** `fileId` (required), `maxBytes` (default 1 MiB; values `< 1` are floored to 1).
- **Returns:** `{ fileId, bytes, truncated, encoding: "utf-8", content }`. **Lossy for binaries** — UTF-8-decodes the bytes.
- **Errors:** invalid `fileId` → `{ ok:false, error }`.

### `download_file_base64`
- **Params:** `fileId` (required), `offset` (default 0; negatives floored to 0), `maxBytes` (1 .. 4 MiB, clamped, default 4 MiB).
- **Returns:** `{ fileId, offset, returnedBytes, totalSize, encoding: "base64", content, eof }` — lossless base64 of just this range. See [Downloading binaries](#downloading-binaries--large-files).
- **Errors:** invalid `fileId` → `{ ok:false, error }`.

### `download_to_url`
- **Params:** `fileId` (required).
- **Returns:** `{ fileId, name, size, url, expiresInSeconds, expiresAt }`. `url` is an unguessable, short-lived capability link served at `GET /gdrive-dl/{token}`.
- **Errors:** invalid `fileId` → `{ ok:false, error }`.

### `create_file` (mutates)
- **Params:** `name` (required), `content` (required UTF-8 text; empty string allowed), `mimeType` (default `text/plain`), `folderId?`.
- **Returns:** the created file's summary object.
- **Errors:** invalid `name` / oversize `content` / bad `mimeType` / bad `folderId` → `{ ok:false, error }`.

### `update_file` (mutates)
- **Params:** `fileId` (required), `newName?` (omit to keep), `newContent?` (omit to keep), `mimeType` (default `text/plain`, applied when replacing content).
- **Returns:** the updated file's summary object.
- **Errors:** invalid `fileId` / `newName` / oversize `newContent` / bad `mimeType` → `{ ok:false, error }`.

### `delete_file` (mutates)
- **Params:** `fileId` (required), `permanent` (default false → trash).
- **Returns:** `{ fileId, deleted: "trashed", name, trashed }` (trash) or `{ fileId, deleted: "permanent" }`.
- **Errors:** invalid `fileId` → `{ ok:false, error }`.

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `GDRIVE_MCP_CRED_DIR` | no | `$HOME/.gdrive-mcp` | Base dir for the credential files. |
| `GDRIVE_MCP_OAUTH_CLIENT` | no | `<CRED_DIR>/gcp-oauth.keys.json` | OAuth 2.0 Desktop client secrets JSON. |
| `GDRIVE_MCP_TOKEN` | no | `<CRED_DIR>/token.json` | Drive-scoped refresh token (bare string or JSON with `refresh_token`). |
| `GDRIVE_MCP_APP_NAME` | no | `gdrive-mcp` | `ApplicationName` reported to the Drive API. |
| `GDRIVE_MCP_DOWNLOAD_BASE_HOST` | no | `127.0.0.1` | Host used to build `download_to_url` links when the full URL is unset. |
| `GDRIVE_MCP_DOWNLOAD_BASE_URL` | no | `http://<BASE_HOST>:<PORT>` | Externally-reachable base URL for staged downloads. Set to a host/port a downloading client can reach. |
| `GDRIVE_MCP_DOWNLOAD_TTL_SECONDS` | no | `600` | Lifetime of a `download_to_url` ticket. A non-positive / non-numeric value falls back to the default. |
| `GDRIVE_MCP_HTTP_TIMEOUT_SECONDS` | no | `100` | Per-request ceiling on the Drive `HttpClient.Timeout` (1 .. 3600). **Fail-closed:** a non-numeric / non-positive / out-of-range value throws at startup, naming the var. |
| `GDRIVE_MCP_HOST` | no | `0.0.0.0` | Listen address. |
| `GDRIVE_MCP_PORT` | no | `9217` | Listen port. |

The credential files (`GDRIVE_MCP_OAUTH_CLIENT`, `GDRIVE_MCP_TOKEN`) must exist and be valid
at startup — the process fails fast otherwise (see [Auth](#auth--one-time-setup)).

## Run

```sh
GDRIVE_MCP_CRED_DIR=/etc/gdrive-mcp \
GDRIVE_MCP_DOWNLOAD_BASE_URL=http://my-host.example:9217 \
GDRIVE_MCP_HTTP_TIMEOUT_SECONDS=100 \
dotnet run -c Release --project src/servers/Gdrive.Mcp
# → MCP endpoint on http://0.0.0.0:9217/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped
so it cannot 400 an otherwise-valid request.

## Auth — one-time setup

Auth uses a Google OAuth 2.0 **Desktop** client plus a **Drive-scoped refresh token**,
both supplied as files (no `.NET FileDataStore`, no browser at runtime):

1. **Enable the Google Drive API** in your GCP project.
2. **Create (or reuse) a Desktop OAuth client** and download its `gcp-oauth.keys.json`.
3. **Mint a Drive-scoped refresh token** once via a browser consent on any machine
   (any standard OAuth tool — only the `refresh_token` string is needed). The scope must be
   the full `https://www.googleapis.com/auth/drive` (the narrower `drive.file` only covers
   app-created files, so `update`/`delete` of pre-existing files would fail).
4. **Provision the two files** (dir `0700`, files `0600`):
   - `gcp-oauth.keys.json` ← the Desktop client secrets
   - `token.json` ← the minted refresh token (bare string, or JSON with a `refresh_token` field)

The process fails fast at startup if the token is absent or invalid — that's the signal this
step was skipped.

## Security notes

- **Input validation is fail-fast and pre-transport.** Every tool runs its `GdriveValidation`
  guards BEFORE any HTTP call: required/non-empty, length caps (named consts), control-char /
  newline rejection, and a leading-`-` rejection on ids (which reach a URL path segment). Bad
  input returns a structured `{ ok:false, error }` — no wasted round-trip, no thrown exception
  through the transport.
- **No secret ever leaves in an error.** Every surfaced upstream/error string is routed through
  `GdriveErrors.Sanitize`, which redacts PEM private-key blocks and any
  `password|secret|token|refresh_token|access_token|client_secret|api-key|bearer|authorization`
  assignment (value redacted to end-of-line), then length-caps the message. A leaked OAuth or
  refresh token in a diagnostic is scrubbed before it reaches the caller.
- **Bounded HTTP timeout.** The Drive `HttpClient.Timeout` is clamped from a fail-closed config
  ceiling (`GDRIVE_MCP_HTTP_TIMEOUT_SECONDS`, default 100 s, hard max 3600 s), so a hung Google
  call cannot pin a request indefinitely.
- **Scope breadth:** full `drive` scope is broad (read/write/delete of *all* the user's Drive).
  Narrow to `drive.file` if you only need app-created files — but then `update`/`delete` only
  work on files this server created.
- **Streaming download endpoint** (`GET /gdrive-dl/{token}`) sits outside `/mcp` on the host
  bind; the access control is a 128-bit unguessable, short-lived ticket token. The filename is
  sanitised into the `Content-Disposition` header so it cannot break the header or path-traverse.
- **Google-native docs** (Docs/Sheets/Slides) can't be `download_file`'d raw — they need an
  Export. Out of scope here; add an `export_file` tool if needed.

## Error contract

Every tool returns a uniform envelope on failure:

```json
{ "ok": false, "error": "<sanitised, length-capped reason>" }
```

- **Validation failures** (bad `fileId`, empty `query`, oversize `content`, …) return this
  envelope immediately, before any HTTP call, with the exact human-readable reason from
  `GdriveValidation`.
- **Upstream failures** (a Google API error, an auth refresh failure, an upload exception)
  are caught and returned in the same envelope, with the message passed through
  `GdriveErrors.Sanitize` so no credential leaks and the length is capped.
- **Success** returns the tool's documented result object (no `ok` field) — see the per-tool
  reference. The two shapes are distinguishable by the presence of `ok: false`.

## Downloading binaries / large files

`download_file` UTF-8-decodes the bytes, so it **corrupts any binary** (firmware `.img`/`.bin`,
archives, images) and is capped. Two lossless paths replace it:

- **`download_file_base64`** — ranged, chunked, lossless. Returns
  `{ fileId, offset, returnedBytes, totalSize, encoding:"base64", content, eof }` where `content`
  is the base64 of *just this range*. To pull a large file, loop: `offset=0`, then
  `offset += returnedBytes`, base64-decode + concatenate each chunk, stop at `eof:true`.
  Default + max chunk **4 MiB** — sized to a modest container memory budget (chunk + ~1.33x
  base64 + JSON response; 8 MiB tipped that envelope into an OOM restart). The reassembled
  bytes are byte-exact. Implemented with the official client's `FilesResource.GetRequest`
  **`DownloadRangeAsync(stream, RangeHeaderValue, ct)`** (its `MediaDownloader` issues the
  ranged `alt=media` request, applies the credential + retry policy — no hand-built REST URL).

- **`download_to_url`** — the right primitive for tens-of-MB artifacts: no base64 ever passes
  through the model context. Returns `{ fileId, name, size, url, expiresInSeconds, expiresAt }`;
  the host streams the file straight from Drive (constant memory) at `GET /gdrive-dl/<token>`.
  `wget`/`curl` the `url` from any host that can reach the configured download base URL. The
  token is a 128-bit unguessable capability valid for a short window (default 600 s,
  `GDRIVE_MCP_DOWNLOAD_TTL_SECONDS`); re-call to mint a fresh one. The endpoint sits on the
  host bind, outside `/mcp` — the random short-lived token is the access control.

## Testing

The `Gdrive.Mcp.Tests` project proves the hardening paths actually fire, without a live Google
account:

- **`GdriveValidationTests`** — the full accept/reject matrix for every `Validate*` method
  (required/empty, length caps, control chars incl. the C# `\0` NUL escape, leading-`-`).
- **`GdriveConfigHardeningTests`** — the fail-closed config matrix for
  `GDRIVE_MCP_HTTP_TIMEOUT_SECONDS`: default, ceiling, and a THROW naming the var on
  `0` / negative / non-numeric / fractional / over-ceiling values.
- **`GdriveErrorsTests`** — the "no secret in an error" contract: PEM key blocks and OAuth /
  refresh / client-secret / bearer assignments are redacted; the length cap fires; a
  non-secret diagnostic survives intact.
- **`DriveToolGuardTests`** — the load-bearing suite (the analogue of the exemplar's
  `SubprocessToolErrorTests`):
  - a bad parameter **short-circuits before any HTTP call** — proven by pointing the tool at a
    fake `DriveService` whose transport throws if reached;
  - a **valid** id reaches that throwing transport and the tool turns the throw into a
    structured error (not an unhandled exception);
  - an **upstream error body carrying a fake secret is redacted** — proven with a fake
    transport that returns a 403 whose body embeds a token, asserting the tool's error string
    is `[redacted]`, not the raw secret.
- **`GdriveConfigTests`**, **`DownloadTicketStoreTests`**, **`ToolSurfaceTests`** — the
  pre-existing neutral-config, capability-ticket, and 9-tool parity guards.
