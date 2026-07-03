# Sinapsi.Indexer

A personal-lab MCP **server** that gives an agent real-time, searchable memory over
a set of source-of-truth git repos: it indexes their markdown, keeps the index
fresh from git-push events on a NATS JetStream stream, and serves the result as MCP
tools over HTTP at `/mcp`. The data tier is Postgres (`tsvector` full-text +
`pgvector` hybrid semantic search); a background worker embeds documents with a
local ONNX model (nothing leaves the host). Everything is env-driven with neutral
`example.com`-style defaults — no instance, repo, subject or host is baked in.

## Contents

- [Overview](#overview)
- [Tool surface (4)](#tool-surface-4)
- [HTTP search endpoint](#http-search-endpoint)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The server is one process with two halves: an **event-driven indexer** (a durable
JetStream consumer that keeps Postgres in sync with the watched repos) and a
**co-hosted MCP surface** (read search + a write learning-publish tool) that shares
the same store. Rebuild = re-scan the sources, never replay the event log.

| Seam / file | Responsibility |
|-------------|----------------|
| `Program.cs` | Composition root: DI wiring, `/mcp` MCP endpoint, health endpoint, env-driven listen address (fail-closed port). |
| `IndexTools.cs` | The MCP **read** surface (`search_index`, `semantic_search`, `get_learning`). Validates every param, then routes any store error through the sanitizer. |
| `LearnTools.cs` | The MCP **write** surface (`publish_learning`): validates, then performs the action by emitting a NATS event. |
| `IndexerValidation.cs` | Fail-fast per-param input validation (null=ok else reason; never throws). Called at the top of every tool before any side effect. |
| `IndexerErrors.cs` | Uniform error sanitization: redacts key material / credential assignments and length-caps every surfaced string. |
| `IndexerConfig.cs` | Fail-closed numeric config (default / floor / ceiling; throws naming the offending env var on bad input). |
| `IndexerWorker.cs` | The JetStream durable consumer + coalescing re-scan loop + periodic safety rescan + background embed loop. |
| `SourceScanner.cs` | git sync + markdown walk → `Document`s (classify + title + content-hash); path denylist for secret-shaped files; token scrubbed from git error text. |
| `PostgresIndexStore.cs` | `IIndexStore` over Postgres: schema, idempotent upsert, tombstone, FTS + hybrid-RRF read queries, embedding backfill. |
| `OnnxEmbedder.cs` | Local all-MiniLM-L6-v2 embedder (in-process ONNX); CPU-contained; env-driven model paths + dims (fail-closed). |
| `LearnPublisher.cs` | Lazily-connected NATS publisher for learning-published events; uses a scoped publish-only identity when configured. |

## Tool surface (4)

| Tool | Mutates | What |
|------|:------:|------|
| `search_index`     | no  | Keyword (FTS) search across all watched sources; optional `source`/`kind` filters; ranked, with `ts_headline` snippets. |
| `semantic_search`  | no  | Hybrid (meaning + keyword) search — local embeddings + pgvector cosine fused with FTS via Reciprocal Rank Fusion. |
| `get_learning`     | no  | List / search the learnings corpus by scope bucket. |
| `publish_learning` | **yes** | Persist a durable learning by emitting a learning-published event on NATS (a downstream materializer writes the repo, which the indexer then re-scans + serves). |

## HTTP search endpoint

`GET /search` — M-B3 plain-HTTP full-text search over the index. Backed by the
same `IIndexStore.SearchAsync` seam as the `search_index` MCP tool; no additional
filtering layer — tombstoned and secret-path rows are excluded **in the SQL** itself
(defence-in-depth: the scanner blocks at ingest, the SQL blocks at read).

### Request

```
GET /search?q=<websearch query>[&limit=<1-30>][&source=<logical-source-name>]
```

| Parameter | Required | Default | Notes |
|-----------|:--------:|---------|-------|
| `q` | **yes** | — | Websearch-syntax query (words, `"phrases"`, `OR`, `-negation`). |
| `limit` | no | `10` | Max results; must be a positive integer ≤ 30. |
| `source` | no | — | Restrict results to one logical source name. |

### Response — 200 OK

```json
{
  "query": "homelab nats",
  "resultCount": 2,
  "results": [
    {
      "source": "home-server",
      "path": "docs/14-nats.md",
      "kind": "doc",
      "title": "NATS Event Fabric",
      "scope": "",
      "snippet": "...homelab nats configuration...",
      "score": 0.0759
    }
  ]
}
```

### Error responses

| Status | Body | When |
|--------|------|------|
| 400 | `{ "error": "<reason>" }` | Missing/empty `q`; non-numeric or out-of-range `limit`; overlong/control-char `source`. |
| 500 | `{ "error": "<scrubbed>" }` | Store failure; error is sanitized (no secrets echoed). |

### Security notes

- Tombstoned rows (`is_deleted = true`) are excluded by the `WHERE NOT is_deleted` SQL clause.
- Secret-path rows (`/secrets/`, `/secret/`, `vault.yml`, `vault.yaml`, `/.git/`, `/private/`)
  are excluded by path `NOT LIKE` conditions in the same SQL query — defence-in-depth against
  a future scanner regression or a manually-inserted row.
- Store errors route through `IndexerErrors.Sanitize` before surfacing to the caller.

## Per-tool reference

### `search_index` (read)

- **Params:** `query` (required, websearch syntax), `source?` (logical repo name),
  `kind?` (one of `doc|pattern|convention|decision|caveat|scope|state|learning|backlog`),
  `limit` (default 10, max 30).
- **Returns:** `{ query, result_count, results[] }` where each result carries
  `source, path, kind, title, scope, snippet, score`.
- **Errors:** `{ error }` when a param fails validation (empty/over-long/control-char
  query, unknown `kind`, out-of-range `limit`) or the store call fails (sanitized).

### `semantic_search` (read)

- **Params:** `query` (required, natural language), `limit` (default 10, max 30).
- **Returns:** `{ query, mode: "hybrid-rrf", result_count, results[] }`.
- **Errors:** `{ error }` — empty query yields exactly `query is required`; a bad
  `limit` short-circuits before embedding; a store failure is sanitized.

### `get_learning` (read)

- **Params:** `scope?` (bucket, e.g. `global` or a project slug), `query?` (optional
  full-text; omit to list the scope), `limit` (default 10, max 30).
- **Returns:** `{ scope, query, result_count, results[] }` where each result carries
  `path, title, scope, excerpt, content_sha, updated_at`.
- **Errors:** `{ error }` on filter/limit validation failure or a sanitized store error.

### `publish_learning` (write — MUTATES)

- **Params:** `slug` (kebab-case `[a-z0-9-]`, the entry id + NATS subject token),
  `title` (one-line), `body` (markdown; NO secrets), `scope` (default `global`,
  NATS-safe token), `tags?`, `session_context?`.
- **Returns:** `{ published: true, subject, slug }`.
- **Errors:** `{ error }` when the slug/scope is not a NATS-safe token, when title/body
  are missing, when a length/control-char cap is exceeded, or when the NATS publish
  fails (sanitized). **This tool mutates** — it emits an event that drives a durable write.

## Configuration

All values are env-driven; numeric knobs are **fail-closed** (a non-numeric or
out-of-range value throws at startup, naming the var). Neutral defaults carry no
site- or instance-specific wiring; the per-server config directory convention is
`/var/lib/sinapsi-indexer/…`.

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `NATS_URL` | no | `nats://127.0.0.1:4222` | NATS server URL. |
| `NATS_NKEY` / `NATS_NKEY_SEED_PATH` | no | — | NKey public key + seed file for auth. |
| `NATS_TLS_CA_FILE` / `NATS_TLS_DISABLE` | no | — | Pinned-CA TLS, or opt-in plaintext. |
| `INDEXER_STREAM` | no | `EVENTS` | JetStream stream the durable consumer binds. |
| `INDEXER_DURABLE` | no | `sinapsi-indexer` | Durable consumer name. |
| `INDEXER_WATCH_SUBJECT` | no | `events.git.>` | Subject filter for git-push notifications (`<prefix>.git.<repo>.push.<branch>`). |
| `INDEXER_RESCAN_INTERVAL_MIN` | no | `60` | Safety full-rescan cadence, minutes (fail-closed 5..1440). |
| `INDEXER_DEBOUNCE_SEC` | no | `15` | Coalescing window for bursts of pushes (fail-closed 2..3600). |
| `INDEXER_EMBED_IDLE_SEC` | no | `30` | Idle sleep between embed passes (fail-closed 5..3600). |
| `INDEXER_EMBED_THROTTLE_MS` | no | `50` | Per-doc throttle in the embed loop (fail-closed 0..60000). |
| `INDEXER_DB_HOST` / `INDEXER_DB_PORT` | no | `127.0.0.1` / `5432` | Postgres host/port. |
| `INDEXER_DB_NAME` / `INDEXER_DB_USER` | no | `sinapsi_index` / `indexer` | Database + user. |
| `INDEXER_DB_PASSWORD` | no | — | Database password (secret; inject at deploy). |
| `INDEXER_LEARNINGS_SOURCE` | no | `learnings` | Which source name holds the learnings corpus. |
| `FORGE_BASE_URL` | no | `https://forge.example.com` | Forge root for repo clone URLs. |
| `INDEXER_REPOS` | no | — | Comma list `source=owner/repo` of repos to index (empty until configured). |
| `INDEXER_REPO_BRANCH` | no | `main` | Branch to clone/track. |
| `INDEXER_CACHE_DIR` | no | `/var/lib/sinapsi-indexer/repos` | Local checkout cache dir. |
| `FORGE_REPO_TOKEN` | no | — | Read-only forge token for cloning (secret). |
| `INDEXER_GIT_USER` | no | `git` | Username used with the token in the clone URL. |
| `LEARN_SUBJECT_PREFIX` | no | `events.learn` | Subject prefix for `publish_learning` (`<prefix>.<scope>.published`). |
| `LEARN_EVENT_SOURCE` | no | `sinapsi-indexer://local/` | CloudEvents producer URI for emitted learnings. |
| `LEARN_NATS_NKEY` / `LEARN_NATS_SEED_PATH` | no | — | Optional scoped publish-only identity for learn events. |
| `EMBED_MODEL_PATH` / `EMBED_VOCAB_PATH` | no | `/opt/models/all-MiniLM-L6-v2/…` | ONNX model + vocab paths. |
| `EMBED_MAX_TOKENS` | no | `256` | Tokenizer cap (fail-closed 1..8192). |
| `EMBED_DIM` | no | `384` | Embedding dimension (fail-closed 1..65536). |
| `INDEXER_HEALTH_HOST` | no | `0.0.0.0` | Health + `/mcp` listen host. |
| `INDEXER_HEALTH_PORT` | no | `8009` | Health + `/mcp` listen port (fail-closed 1..65535). |

## Run

```sh
INDEXER_REPOS="docs=acme/docs,learnings=acme/learnings" \
FORGE_BASE_URL="https://forge.example.com" \
INDEXER_DB_HOST="127.0.0.1" INDEXER_DB_PASSWORD="<secret>" \
NATS_URL="nats://127.0.0.1:4222" \
dotnet run -c Release --project src/servers/Sinapsi.Indexer
# -> MCP endpoint on http://0.0.0.0:8009/mcp ; health on GET :8009/
# -> HTTP search on GET :8009/search?q=...
```

## Security notes

- **No secret in an error.** Every surfaced upstream/error string routes through
  `IndexerErrors.Sanitize`, which redacts PEM private-key blocks and
  `password|secret|token|api-key|bearer|authorization` assignments and length-caps
  the message. A Postgres error echoing the connection-string password, or a git
  error echoing the forge token, is scrubbed before it leaves the process.
- **Validate before side effects.** Every tool validates all of its parameters via
  `IndexerValidation` *before* any DB round-trip, embedding, or NATS publish, so
  malformed input never reaches the data tier or the bus.
- **Fail-closed config.** Numeric knobs reject non-numeric / out-of-range values at
  startup (naming the var) rather than silently clamping a footgun (e.g. a health
  port outside 1..65535, a zero debounce spinning the coalesce loop).
- **Never indexes secret-shaped paths.** The scanner denylists `secrets/`, `secret/`,
  `vault.yml`, `vault.yaml`, `private/`, `.git/`.
- **Scoped publish identity.** `publish_learning` uses a scoped publish-only NATS
  identity (`LEARN_NATS_*`) when configured, not a broad admin nkey.
- **Local embeddings.** Embedding runs in-process (ONNX) so indexed content never
  leaves the host.
- **Parameterised SQL + kebab-only subject tokens** are the primary injection
  defences; the validation layer is defence-in-depth.

## Error contract

Every tool returns a normal result object on success, and a uniform
`{ error: "<human-readable reason>" }` envelope on any validation failure or
sanitized upstream failure — the tool never throws to the caller and never leaks a
secret in the `error` string. Documented exception to the envelope shape: a genuine
`OperationCanceledException` (client cancelled) is propagated rather than wrapped,
so cancellation is not misreported as a tool error.

## Testing

The per-server test project (`test/Sinapsi.Indexer.Tests`) proves the hardening
paths actually fire — not just that helpers exist:

- **Config fail-closed matrix** (`IndexerConfigTests`) — every numeric knob:
  default-when-unset, accept-in-range, and THROW naming the var on
  non-numeric / below-floor / above-ceiling.
- **Validation surface** (`IndexerValidationTests`) — required/empty, length caps,
  control-char/newline rejection (NUL via the `\0` escape), the closed `kind` set,
  and limit/tag bounds.
- **Invalid-input → structured-error tool guards** (`IndexToolsGuardTests`,
  `LearnToolsGuardTests`) — an `InlineData` matrix drives each tool with bad input
  and points the store/embedder at a fake that **throws a sentinel if reached**,
  proving the guard short-circuits before any DB round-trip / embedding / publish.
- **Error-scrub end-to-end** (`IndexToolsGuardTests` + `IndexerErrorsTests`) — the
  load-bearing leg: a *valid* input reaches a fake store whose exception carries a
  fake secret, and the tool returns `[redacted]`, not the raw secret; plus the unit
  contract (no key-material / credential leak + length cap).
- **Tool-surface parity** (`ToolSurfaceTests`) — asserts exactly the four tools by
  name across both tool types, each with an `[McpServerTool]` name + `[Description]`.
- **GET /search route** (`SearchRouteTests`, M-B3) — hermetic `TestServer` tests
  (no NATS, no Postgres, no ONNX): 400 on missing/empty/invalid params; 200 with
  correct JSON shape and `resultCount`; `source` + `limit` forwarding to the store;
  500 with secret-redacted error on store failure; seam-contract proof (clean rows only).
- **Secret-path denylist contract** (`SearchStoreDenylistTests`) — unit proof that
  the SQL denylist fragments in `PostgresIndexStore.SearchAsync` are identical to the
  scanner's ingest-time denylist, the path predicate matches expected paths, and the
  SQL clause covers all fragments. Live-DB integration tests (tombstone + secret-path
  SQL exclusion, ranked ordering) are in `SearchStoreIntegrationTests` — skipped
  hermetically (require `INDEXER_DB_HOST` + `INDEXER_DB_PASSWORD`).

Read-tool / data-layer integration coverage that needs a live pgvector + ONNX model
(actual FTS ranking, RRF fusion, real embeddings) is intentionally out of scope for
this suite (un-runnable in the build environment); it is covered by deployment
smoke tests.

This is exploratory code written for personal learning, offered as-is under the
repository's `LICENSE`.
