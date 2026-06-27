# Sinapsi.Indexer

A personal-lab MCP **server** that gives an agent **real-time, searchable memory**
over a set of source-of-truth git repos. It is an event-DRIVEN cache: it indexes
markdown from the repos you point it at, keeps the index fresh from git-push
events on a NATS JetStream stream, and serves the result as MCP tools over HTTP
at `/mcp`. The data tier is a Postgres database (`tsvector` full-text + `pgvector`
hybrid semantic search).

Everything is env-driven with neutral local defaults — no instance, repo, subject
or host is baked in.

## How it works

- **Sources are the truth.** It indexes the repos named in `INDEXER_REPOS`
  (markdown only). Events are **change-notifications**, never the data.
- **Event-driven:** a durable consumer on a JetStream stream (`INDEXER_STREAM`),
  filtered to git-push notifications (`INDEXER_WATCH_SUBJECT`, default
  `events.git.>`). A push to a watched repo **marks it dirty**; a coalescing loop
  re-scans each dirty repo at most once per debounce window (git pull → walk →
  upsert).
- **Rebuild = re-scan the sources**, never replay the event log. A full re-scan
  runs at startup and on a periodic safety timer (`INDEXER_RESCAN_INTERVAL_MIN`).
- **Idempotent + tombstoned:** upserts are keyed by `doc_id` (`<source>:<path>`)
  and skip when `content_sha` is unchanged; files that disappear from a source are
  tombstoned (`is_deleted = true`), not hard-deleted.
- **Hybrid embeddings:** a background worker embeds documents with a local
  all-MiniLM-L6-v2 ONNX model (entirely in-process, nothing leaves the host) so
  `semantic_search` can fuse meaning with keywords (Reciprocal Rank Fusion).
- **Never indexes** secret-shaped paths (denylist: `secrets/`, `vault.yml`,
  `private/`, …).
- **publish_learning** is the write half: the tool emits a learning-published
  event on NATS; a downstream materializer writes it back to the learnings repo,
  which the indexer then re-scans + serves.

## Tools

| Tool | Kind | Purpose |
|------|------|---------|
| `search_index`    | read  | Keyword (FTS) search across all watched sources; optional `source`/`kind` filters; ranked, with snippets. |
| `semantic_search` | read  | Hybrid (meaning + keyword) search via local embeddings + RRF. |
| `get_learning`    | read  | List/search the learnings corpus by scope bucket. |
| `publish_learning`| write | Persist a durable learning by emitting a learning-published event. |

## Configuration

| Env var | Default | Purpose |
|---------|---------|---------|
| `NATS_URL` | `nats://127.0.0.1:4222` | NATS server URL. |
| `NATS_NKEY` / `NATS_NKEY_SEED_PATH` | — | NKey public key + seed file for auth. |
| `NATS_TLS_CA_FILE` / `NATS_TLS_DISABLE` | — | Pinned-CA TLS, or opt-in plaintext. |
| `INDEXER_STREAM` | `EVENTS` | JetStream stream the durable consumer binds. |
| `INDEXER_DURABLE` | `sinapsi-indexer` | Durable consumer name. |
| `INDEXER_WATCH_SUBJECT` | `events.git.>` | Subject filter for git-push notifications (`<prefix>.git.<repo>.push.<branch>`). |
| `INDEXER_RESCAN_INTERVAL_MIN` | `60` | Safety full-rescan cadence (minutes). |
| `INDEXER_DEBOUNCE_SEC` | `15` | Coalescing window for bursts of pushes. |
| `INDEXER_DB_HOST` / `INDEXER_DB_PORT` | `127.0.0.1` / `5432` | Postgres host/port. |
| `INDEXER_DB_NAME` / `INDEXER_DB_USER` | `sinapsi_index` / `indexer` | Database + user. |
| `INDEXER_DB_PASSWORD` | — | Database password (secret; inject at deploy). |
| `INDEXER_LEARNINGS_SOURCE` | `learnings` | Which source name holds the learnings corpus. |
| `FORGE_BASE_URL` | `https://forge.example.com` | Forge root for repo clone URLs. |
| `INDEXER_REPOS` | — | Comma list `source=owner/repo` of repos to index. |
| `INDEXER_REPO_BRANCH` | `main` | Branch to clone/track. |
| `INDEXER_CACHE_DIR` | `/var/lib/sinapsi-indexer/repos` | Local checkout cache dir. |
| `FORGE_REPO_TOKEN` | — | Read-only forge token for cloning (secret). |
| `INDEXER_GIT_USER` | `git` | Username used with the token in the clone URL. |
| `LEARN_SUBJECT_PREFIX` | `events.learn` | Subject prefix for `publish_learning` (`<prefix>.<scope>.published`). |
| `LEARN_EVENT_SOURCE` | `sinapsi-indexer://local/` | CloudEvents producer URI for emitted learnings. |
| `LEARN_NATS_NKEY` / `LEARN_NATS_SEED_PATH` | — | Optional scoped publish-only identity for learn events. |
| `EMBED_MODEL_PATH` / `EMBED_VOCAB_PATH` | `/opt/models/all-MiniLM-L6-v2/…` | ONNX model + vocab paths. |
| `EMBED_MAX_TOKENS` / `EMBED_DIM` | `256` / `384` | Tokenizer cap + embedding dimension. |
| `INDEXER_HEALTH_HOST` / `INDEXER_HEALTH_PORT` | `0.0.0.0` / `8009` | Health + `/mcp` listen address. |

## Run

```sh
dotnet run -c Release --project src/servers/Sinapsi.Indexer
# → MCP endpoint on http://0.0.0.0:8009/mcp ; health on GET :8009/
```

This is exploratory code written for personal learning, offered as-is under the
repository's `LICENSE`.
