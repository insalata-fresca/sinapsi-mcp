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
- [Capability flags](#capability-flags)
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
| `IndexerCapabilities.cs` | Resolves the `INDEXER_CAP_*` + `INDEXER_NATS_MODE` env flags into a plain, unit-testable composition decision (which index-worker shape, which MCP tool types, whether any NATS connection is needed at all). `Program.cs` reads it and acts — no other file branches on the flags. |
| `IndexerCore.cs` | The reindex/embed engine (scan → upsert → tombstone, background embed backfill) shared by BOTH index-worker shapes below. Holds no NATS state. |
| `IndexerWorker.cs` | index capability, shared-bus shape (default): the JetStream durable consumer + coalescing re-scan loop + periodic safety rescan, delegating the actual reindex/embed work to `IndexerCore`. |
| `TimerOnlyIndexWorker.cs` | index capability, isolated shape (`INDEXER_NATS_MODE=isolated`): the SAME `IndexerCore` engine, timer-only (no push signal, no coalesce loop), and **no NATS client of any kind** — see [Capability flags](#capability-flags). |

## Capability flags

**Design:** `docs/architecture/indexer-generalization.md` (home-server repo).
The image composes at startup from four independently-enable-able capabilities
plus a NATS-reach mode. **A disabled capability wires nothing** — no route, no
MCP tool, no NATS connection/consumer/seed, no identity. This is
defense-in-depth: the capability is *absent from the running process*, not
merely unreachable behind a firewall.

| Env var | Values | Default (unset) | What it gates |
|---------|--------|:----------------:|----------------|
| `INDEXER_CAP_INDEX` | `true`\|`false` | `true` | The re-scan/upsert engine (git-pull → walk → classify → upsert → tombstone). `false` ⇒ neither worker shape is constructed — no scanner loop at all. |
| `INDEXER_CAP_SEARCH_MCP` | `true`\|`false` | `true` | The MCP read tools (`search_index`, `semantic_search`, `get_learning` — `IndexTools`). `false` ⇒ `IndexTools` is never added to the MCP server's tool-type list. |
| `INDEXER_CAP_SEARCH_HTTP` | `true`\|`false` | `true` | The `GET /search` route. `false` ⇒ the route is never mapped at all (404 from Kestrel's own "no matching endpoint", same observable shape as today's token-unset 404). |
| `INDEXER_CAP_LEARN_PUBLISH` | `true`\|`false` | `true` | The **only shared-bus WRITE capability**: the `publish_learning` MCP tool (`LearnTools`) AND the `LearnPublisher` NATS identity. `false` ⇒ `LearnPublisher` is never constructed (so `LEARN_NATS_NKEY`/`LEARN_NATS_SEED_PATH` are never even read) and `LearnTools` is never added to the MCP tool-type list. |
| `INDEXER_NATS_MODE` | `shared-bus`\|`isolated` | `shared-bus` | Whether the `index` capability (when enabled) is the NATS-consuming `IndexerWorker` (push-coalesced) or the timer-only `TimerOnlyIndexWorker` (**zero NATS client** — no consumer, no admin seed, no `/etc/nats-client` mount at the Ansible-role layer). Does not affect `learn_publish`, which asserts its own identity independently when enabled. |

**Back-compat is the prime directive.** Every flag above defaults to today's
bundled behaviour when unset (`index=true, search.mcp=true, search.http=true,
learn_publish=true, nats_mode=shared-bus`), so deploying this image with an
**unchanged** `config.env` is a behavioural no-op — this is what lets the image
ship ahead of any role/profile change (design doc §5, step 1).

**Fail-closed parsing.** A boolean flag accepts `true`/`false`/`1`/`0`
(case-insensitive); `INDEXER_NATS_MODE` accepts `shared-bus`/`isolated`
(case-insensitive). Any other non-empty value **throws at startup, naming the
var** — a typo must not silently re-enable or silently disable a capability.

**Composition, not firewalling.** The decision lives in
`IndexerCapabilities` (a plain class with no ASP.NET/DI/NATS dependency) and
`Program.cs` acts on it directly:
- `learn_publish=false` removes `LearnTools` from the MCP tool-TYPE list handed
  to `WithTools(...)` (a runtime `IEnumerable<Type>`, not the compile-time
  generic `.WithTools<T>()`) — the tool type never reaches the MCP server, so
  there is no code path by which it could be invoked, gated or not.
- `nats_mode=isolated` selects `TimerOnlyIndexWorker` instead of
  `IndexerWorker` — a type that derives from `BackgroundService` directly
  (NOT `Sinapsi.Nats.JetStreamWorker`) and never references a
  `Sinapsi.Nats.*` type anywhere in its declared members (proven by a
  reflection test, `TimerOnlyIndexWorkerTests`). No `NatsConnectionOptions` is
  even constructed for it.
- Health (`GET /`) reports only the capabilities that are actually enabled —
  e.g. `nats_ready` is omitted (not `false`) when the index worker is the
  isolated, NATS-free shape.

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
| `INDEXER_CAP_INDEX` | no | `true` | Enable the re-scan/upsert engine. See [Capability flags](#capability-flags). |
| `INDEXER_CAP_SEARCH_MCP` | no | `true` | Enable the MCP read tools (`IndexTools`). |
| `INDEXER_CAP_SEARCH_HTTP` | no | `true` | Enable the `GET /search` route. |
| `INDEXER_CAP_LEARN_PUBLISH` | no | `true` | Enable `publish_learning` + the `LearnPublisher` NATS identity (the only shared-bus write capability). |
| `INDEXER_NATS_MODE` | no | `shared-bus` | `shared-bus` (push-coalesced `IndexerWorker`) or `isolated` (timer-only `TimerOnlyIndexWorker`, zero NATS client for indexing). |

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
- **Capability-flag fail-closed parsing** (`IndexerConfigTests`) — every
  `INDEXER_CAP_*` + `INDEXER_NATS_MODE` knob: default-when-unset (matches
  today's bundled behaviour), accepts `true`/`false`/`1`/`0` /
  `shared-bus`/`isolated`, and THROWS naming the var on any other value.
- **Composition decision** (`IndexerCapabilitiesTests`) — pins the exact
  worker-shape / MCP-tool-type-list / NATS-connection-need decision for every
  capability combination: all-unset reproduces today's bundle;
  `learn_publish=false` excludes `LearnTools` from the MCP tool-type list
  (independent of `search.mcp`); `nats_mode=isolated` selects `TimerOnly`
  (never `SharedBusConsumer`) while search/index still compose; every
  capability off ⇒ empty tool list + no worker + no NATS connection needed.
- **Isolated-mode NATS-free proof** (`TimerOnlyIndexWorkerTests`) — structural
  reflection tests assert `TimerOnlyIndexWorker` derives from
  `BackgroundService` (not `JetStreamWorker`) and that NO field, property,
  method parameter, or return type anywhere on the type is a `Sinapsi.Nats.*`
  type; a functional test then runs the worker to `Ready=true` through fakes
  only, proving the startup re-scan completes with zero NATS involvement.

Read-tool / data-layer integration coverage that needs a live pgvector + ONNX model
(actual FTS ranking, RRF fusion, real embeddings) is intentionally out of scope for
this suite (un-runnable in the build environment); it is covered by deployment
smoke tests.

This is exploratory code written for personal learning, offered as-is under the
repository's `LICENSE`.
