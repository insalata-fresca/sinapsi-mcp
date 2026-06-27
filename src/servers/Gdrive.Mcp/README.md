# Gdrive.Mcp

A self-hosted **Google Drive CRUD MCP server**. Many managed Drive connectors expose only
read/search/download/create — they have **no `update` and no `delete`**, and a third-party
connector can't be extended. This server owns the full file lifecycle, talking to Drive via
the official [`Google.Apis.Drive.v3`](https://www.nuget.org/packages/Google.Apis.Drive.v3)
.NET client and exposing the tool surface over streamable HTTP at `/mcp`.

It holds no deployment topology in source — credential paths, the listen address, and the
externally-reachable download base URL are all supplied by environment variables at deploy
time (defaults are neutral local placeholders).

## Tools (9)

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

### Downloading binaries / large files

`download_file` UTF-8-decodes the bytes, so it **corrupts any binary** (firmware `.img`/`.bin`,
archives, images) and is capped. Two lossless paths replace it:

- **`download_file_base64`** — ranged, chunked, lossless. Returns
  `{fileId, offset, returnedBytes, totalSize, encoding:"base64", content, eof}` where `content`
  is the base64 of *just this range*. To pull a large file, loop: `offset=0`, then
  `offset += returnedBytes`, base64-decode + concatenate each chunk, stop at `eof:true`.
  Default + max chunk **4 MiB** — sized to a modest container memory budget (chunk + ~1.33x
  base64 + JSON response; 8 MiB tipped that envelope into an OOM restart). The reassembled
  bytes are byte-exact. Implemented with the official client's `FilesResource.GetRequest`
  **`DownloadRangeAsync(stream, RangeHeaderValue, ct)`** (its `MediaDownloader` issues the
  ranged `alt=media` request, applies the credential + retry policy — no hand-built REST URL).

- **`download_to_url`** — the right primitive for tens-of-MB artifacts: no base64 ever passes
  through the model context. Returns `{fileId, name, size, url, expiresInSeconds, expiresAt}`;
  the host streams the file straight from Drive (constant memory) at `GET /gdrive-dl/<token>`.
  `wget`/`curl` the `url` from any host that can reach the configured download base URL. The
  token is a 128-bit unguessable capability valid for a short window (default 600 s,
  `GDRIVE_MCP_DOWNLOAD_TTL_SECONDS`); re-call to mint a fresh one. The endpoint sits on the
  host bind, outside `/mcp` — the random short-lived token is the access control.

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `GDRIVE_MCP_CRED_DIR` | no | `$HOME/.gdrive-mcp` | Base dir for the credential files. |
| `GDRIVE_MCP_OAUTH_CLIENT` | no | `<CRED_DIR>/gcp-oauth.keys.json` | OAuth 2.0 Desktop client secrets JSON. |
| `GDRIVE_MCP_TOKEN` | no | `<CRED_DIR>/token.json` | Drive-scoped refresh token (bare string or JSON with `refresh_token`). |
| `GDRIVE_MCP_APP_NAME` | no | `gdrive-mcp` | `ApplicationName` reported to the Drive API. |
| `GDRIVE_MCP_DOWNLOAD_BASE_HOST` | no | `127.0.0.1` | Host used to build `download_to_url` links when the full URL is unset. |
| `GDRIVE_MCP_DOWNLOAD_BASE_URL` | no | `http://<BASE_HOST>:<PORT>` | Externally-reachable base URL for staged downloads. Set to a host/port a downloading client can reach. |
| `GDRIVE_MCP_DOWNLOAD_TTL_SECONDS` | no | `600` | Lifetime of a `download_to_url` ticket. |
| `GDRIVE_MCP_HOST` | no | `0.0.0.0` | Listen address. |
| `GDRIVE_MCP_PORT` | no | `9217` | Listen port. |

## Run

```sh
GDRIVE_MCP_CRED_DIR=/etc/gdrive-mcp \
GDRIVE_MCP_DOWNLOAD_BASE_URL=http://my-host.example:9217 \
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

## Notes

- **Scope breadth:** full `drive` scope is broad (read/write/delete of *all* the user's Drive).
  Narrow to `drive.file` if you only need app-created files — but then `update`/`delete` only
  work on files this server created.
- **Google-native docs** (Docs/Sheets/Slides) can't be `download_file`'d raw — they need an
  Export. Out of scope here; add an `export_file` tool if needed.
