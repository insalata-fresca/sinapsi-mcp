# Gemini.Mcp

A personal-lab MCP **server** that wraps the authenticated Google `gemini` CLI and
exposes it as Model Context Protocol tools over streamable HTTP at `/mcp`.

The server is a thin host over [`Sinapsi.Mcp`](../../libs/Sinapsi.Mcp): it shells out
to the `gemini` CLI as a subprocess for each call, captures stdout/stderr, and returns
the result. The CLI itself (and its OAuth credentials) live on the host — this project
is just the MCP adapter in front of it.

## Tools (10)

| Tool | Sync? | Purpose |
|------|:-----:|---------|
| `ask` | yes | Ask Gemini a question; optional `model` and `system` prefix. |
| `ask_with_files` | yes | Ask with local files attached as context (`@path` mentions). |
| `research` | async | Deep web-research run; returns a `task_id`, poll with `get_status`. |
| `sandbox` | yes | Run a prompt through Gemini's sandbox (code runs in Gemini's sandbox). |
| `image_describe` | yes | Describe an image on disk via Gemini Pro vision. |
| `image_generate` | yes | Generate an image via the nanobanana extension (needs `NANOBANANA_API_KEY`). |
| `session_create` | yes | Open a conversational session; returns a `session_id`. |
| `session_resume` | yes | Send another prompt within a session. |
| `session_close` | yes | Close a session and remove its on-disk state. |
| `get_status` | yes | Poll an async task (e.g. a `research` run). |

`research` is asynchronous because a deep run can take many minutes; it gets its own,
longer timeout (`GEMINI_RESEARCH_TIMEOUT_MS`, default 30 min) instead of being capped by
the shorter interactive timeout.

## Configuration

Everything is env-driven; the defaults are generic local placeholders, not a fixed layout.

| Env var | Default | Purpose |
|---------|---------|---------|
| `GEMINI_BIN` | `/usr/local/lib/node_modules/@google/gemini-cli/bundle/gemini.js` | Path to the gemini-cli bundle entry. Invoked as `node <this>`. |
| `GEMINI_SESSION_DIR` | `/var/lib/gemini-mcp/sessions` | Per-session working dirs + state. |
| `GEMINI_TASK_DIR` | `/var/lib/gemini-mcp/tasks` | Async task state files. |
| `NANO_BANANA_OUTPUT_DIR` | `/var/lib/nano-banana/output` | `image_generate` output root. |
| `GEMINI_TIMEOUT_MS` | `180000` | Per-call interactive timeout. |
| `GEMINI_RESEARCH_TIMEOUT_MS` | `1800000` | Deep-research timeout (30 min). |
| `GEMINI_MCP_PORT` | `9211` | Listen port. |
| `GEMINI_MCP_HOST` | `0.0.0.0` | Listen address. |

The `gemini` CLI resolves its OAuth credentials from `~/.gemini` via the user's home
directory, so the process's `HOME` must point at the account that holds those credentials.

## Run

```sh
dotnet run -c Release --project src/servers/Gemini.Mcp
# → MCP endpoint on http://0.0.0.0:9211/mcp
```

Node.js 20+ must be on the host (the `gemini` CLI requires it). The transport is stateless;
a fronting proxy's forwarded `Mcp-Session-Id` header is stripped so it cannot 400 an
otherwise-valid request.
