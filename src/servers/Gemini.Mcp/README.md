# Gemini.Mcp

A personal-lab MCP **server** that wraps the authenticated Google `gemini` CLI and
exposes it as Model Context Protocol tools over streamable HTTP at `/mcp`. It is a
thin, hardened host over the host-installed CLI: it shells out to `gemini` (as
`node <bundle.js>`) for each call, captures stdout/stderr under a hard timeout,
validates every parameter before spawning, and scrubs any upstream diagnostic of
secrets before it leaves the process. It exposes a 10-tool surface.

This server follows the security, testing and documentation bar set by the
reference-grade exemplar [`StepCa.Mcp`](../StepCa.Mcp).

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-10)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The server holds **no deployment topology in source**. Every path and timeout is
supplied by environment variables at deploy time; the defaults are generic
local-lab placeholders, not a fixed layout, so the binary carries no site-specific
wiring.

Architecturally it is a few small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `GeminiConfig.cs` | Bind + validate env into an immutable record; fail-closed timeout clamp (throws naming the offending var on a bad value). |
| Subprocess | `GeminiCli.cs` | Run the host `gemini` CLI (`node <bundle>`) with a hard timeout, drained pipes, and a kill-tree on timeout. |
| Validation | `GeminiValidation.cs` | One `Validate*` per tool param (required/length/control-char/leading-`-`/path-traversal), returning a reason string, never throwing. |
| Errors | `GeminiErrors.cs` | `Sanitize` every surfaced upstream string — redact key blocks + credential assignments, length-cap. |
| State | `State.cs` | On-disk async-task + session state file formats (snake_case JSON). |
| Tools | `AskTool.cs`, `ImageGenerateTool.cs`, `ResearchTools.cs`, `SessionTools.cs` | The 10 MCP tools. Validate input first, then invoke the CLI, then scrub errors. |

## Tool surface (10)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `ask` | no | Ask Gemini a question; optional `model` + `system` prefix. Returns the model text. |
| `ask_with_files` | no | Ask with local files attached as `@path` context. |
| `research` | **yes** (writes a task file) | Async deep web-research run; returns a `task_id`, poll with `get_status`. |
| `sandbox` | no | Run a prompt through Gemini's sandbox (code runs in Gemini's sandbox). |
| `image_describe` | no | Describe an on-disk image via Gemini Pro vision. |
| `image_generate` | **yes** (writes output files) | Generate an image via the nanobanana extension (needs `NANOBANANA_API_KEY`). |
| `session_create` | **yes** (writes session dir) | Open a conversational session; returns a `session_id`. |
| `session_resume` | **yes** (updates session state) | Send another prompt within a session. |
| `session_close` | **yes** (deletes session dir) | Close a session and remove its on-disk state. |
| `get_status` | no | Poll an async task (e.g. a `research` run). |

## Per-tool reference

### `ask`
- **Params:** `prompt` (string, **required**), `model` (`auto`|`pro`|`flash`|`flash-lite`, default `auto`), `system` (string, optional prefix).
- **Returns:** the model's text response (trimmed).
- **Errors:** input-validation failures and CLI failures throw `InvalidOperationException`; the CLI-failure message is `gemini exited <code>: <scrubbed stderr tail>`.

### `ask_with_files`
- **Params:** `prompt` (**required**), `file_paths` (string[], **required**, ≤100 entries; each non-empty, ≤4096 chars, no control chars, no leading `-`), `model` (as above).
- **Returns:** the model's text response.
- **Errors:** as `ask`; a malformed `file_paths[i]` is reported by index before any spawn.

### `research` (mutates — writes a task file)
- **Params:** `query` (**required**), `depth` (`quick`|`standard`|`deep`, default `standard`).
- **Returns:** `{ task_id, status: "running", poll_with: "get_status", estimated_wait_s }` — the run is fire-and-forget in the background under the (longer) research timeout.
- **Errors:** a validation failure returns a `{ error }` JSON object **without** minting a task. The background run's stderr/exception is **scrubbed** before it is persisted (so `get_status` cannot leak a secret).

### `sandbox`
- **Params:** `prompt` (**required**).
- **Returns:** the model's text response.
- **Errors:** as `ask`; message prefixed `gemini sandbox exited …`.

### `image_describe`
- **Params:** `image_path` (**required**, path-shape validated: non-empty, ≤4096, no control chars, no leading `-`), `question` (optional).
- **Returns:** the model's description text.
- **Errors:** an invalid path shape throws before the existence check; a well-formed-but-absent path throws `image not found: <path>`; CLI failure as `ask`.

### `image_generate` (mutates — writes output files)
- **Params:** `prompt` (**required**), `aspect_ratio` (optional, one of `1:1`|`16:9`|`9:16`|`4:3`|`3:4`).
- **Returns:** `{ path, size_bytes, count, all_paths }` on success.
- **Errors:** validation failures and the no-output case both throw `InvalidOperationException` whose message is a structured JSON object (`{ error, … }`); the no-output error carries a **scrubbed** `gemini_stderr_tail` and a `NANOBANANA_API_KEY` hint. The prompt is escaped before interpolation into the directive; the per-call temp dir is deleted when no image is produced.

### `session_create` (mutates — writes a session dir)
- **Params:** `focus` (string, optional, ≤1024 chars).
- **Returns:** `{ session_id }`.
- **Errors:** an over-long/NUL-bearing `focus` throws before any dir is created.

### `session_resume` (mutates — updates session state)
- **Params:** `session_id` (**required**, id-shape validated: non-empty, ≤128, no control chars, no path separator, not `.`/`..`), `prompt` (**required**).
- **Returns:** the model's text response; increments the session's `prompt_count`.
- **Errors:** an invalid id throws **before** any filesystem access (blocking path traversal); a well-formed-but-absent id throws `session <id> not found`; CLI failure as `ask`.

### `session_close` (mutates — deletes a session dir)
- **Params:** `session_id` (**required**, id-shape validated as above).
- **Returns:** `{ session_id, closed: true }` (idempotent — no error if already absent).
- **Errors:** an invalid id throws before any filesystem access.

### `get_status`
- **Params:** `task_id` (**required**, id-shape validated as above).
- **Returns:** the persisted task-state JSON verbatim.
- **Errors:** an invalid id throws before any filesystem access (blocking path traversal); an unknown well-formed id throws `task <id> not found`.

## Configuration

Everything is env-driven; the defaults are generic local placeholders, not a fixed layout.

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `GEMINI_BIN` | no | `/usr/local/lib/node_modules/@google/gemini-cli/bundle/gemini.js` | Path to the gemini-cli bundle entry. Invoked as `node <this>`. |
| `GEMINI_SESSION_DIR` | no | `/var/lib/gemini-mcp/sessions` | Per-session working dirs + state. |
| `GEMINI_TASK_DIR` | no | `/var/lib/gemini-mcp/tasks` | Async task state files. |
| `NANO_BANANA_OUTPUT_DIR` | no | `/var/lib/nano-banana/output` | `image_generate` output root. |
| `GEMINI_TIMEOUT_MS` | no | `180000` | Per-call interactive subprocess timeout. Must be an integer in `1..3600000` ms; a non-numeric, `<= 0`, or out-of-range value **fails startup** (rather than silently making every call time out or throwing deep in the cancellation path). |
| `GEMINI_RESEARCH_TIMEOUT_MS` | no | `1800000` | Deep-research subprocess timeout (30 min). Same `1..3600000` ms validation as above. |
| `GEMINI_MCP_HOST` | no | `0.0.0.0` | Listen address. |
| `GEMINI_MCP_PORT` | no | `9211` | Listen port. |

`research` is asynchronous because a deep run can take many minutes; it gets its own,
longer timeout (`GEMINI_RESEARCH_TIMEOUT_MS`) instead of being capped by the shorter
interactive one.

## Run

```sh
GEMINI_BIN=/usr/local/lib/node_modules/@google/gemini-cli/bundle/gemini.js \
dotnet run -c Release --project src/servers/Gemini.Mcp
# → MCP endpoint on http://0.0.0.0:9211/mcp
```

Node.js 20+ must be on the host (the `gemini` CLI requires it), and the CLI resolves
its OAuth credentials from `~/.gemini`, so the process's `HOME` must point at the
account that holds them. The transport is stateless; a fronting proxy's forwarded
`Mcp-Session-Id` header is stripped so it cannot 400 an otherwise-valid request.

## Security notes

This server drives an authenticated LLM CLI and writes to disk. It is built to fail safe:

- **Fail-closed config.** A set-but-invalid `GEMINI_TIMEOUT_MS` /
  `GEMINI_RESEARCH_TIMEOUT_MS` (non-numeric, `<= 0`, or above the `3600000` ms
  ceiling) throws on startup naming the offending var, rather than being swallowed
  into a default that would make every call time out instantly or throw deep in the
  cancellation path.
- **No secret leakage in errors.** Every surfaced upstream string — the CLI stderr
  tail in `ask`/`ask_with_files`/`sandbox`/`image_describe`/`session_resume`, the
  `gemini_stderr_tail` in `image_generate`, and the persisted `error` in a
  `research` task file that `get_status` returns — is passed through
  `GeminiErrors.Sanitize`. PEM **private-key** blocks and
  `password=/secret=/token=/api-key=/bearer/authorization:` style assignments
  (including env-style names such as this server's own `NANOBANANA_API_KEY`) are
  redacted, and the message is length-capped.
- **No shell.** Subprocess arguments are passed via `ProcessStartInfo.ArgumentList`
  (no shell, no string interpolation), so a hostile prompt/path cannot inject a
  shell command. As defence in depth, path values starting with `-` are rejected so
  they can't be mistaken for a `gemini` flag, and the `image_generate` prompt is
  backslash/quote-escaped before it is interpolated into the directive string.
- **Input validation before side effects.** Every tool validates its parameters via
  `GeminiValidation` **before** spawning a subprocess, minting a task, or touching
  the filesystem. `session_id` / `task_id` are validated as clean ids (no path
  separator, no `.`/`..`) **before** they are interpolated into a filesystem path,
  blocking path traversal outside the session/task dir.
- **Bounded subprocess.** Every `gemini` call runs under a hard timeout with the
  process tree killed and awaited on timeout (Node.js can spawn its own children), so
  a hung CLI cannot wedge the server. The async pipes are drained with a synchronous
  `WaitForExit()` so captured output is not truncated.
- **Deterministic cleanup.** `image_generate`'s per-call temp dir is removed when no
  image is produced; `session_close` removes the whole session dir.

## Error contract

Every tool returns either its success payload or a structured error. The **channel**
is per-tool (unchanged by this hardening pass): the string-returning tools
(`ask`, `ask_with_files`, `sandbox`, `image_describe`, `session_resume`,
`image_generate`, `get_status`, `session_create`, `session_close`) surface errors by
throwing `InvalidOperationException` — for `image_generate` the message is a
structured JSON object; `research` returns a `{ error }` JSON object on a validation
failure. In all cases any upstream-`gemini` diagnostic is scrubbed of
key/credential material and length-capped before it is surfaced.

## Testing

```sh
dotnet test test/Gemini.Mcp.Tests
```

The suite covers the tool-surface parity guard (exactly 10 named tools, each with a
description), the state-file JSON shape, session/async-task lifecycle, and the
**hardening paths**:

- **Config fail-closed matrix** (`GeminiConfigTests`): a set-but-invalid timeout
  (non-numeric / `<= 0` / above ceiling) throws naming the var; empty uses the
  default; the ceiling value is accepted; defaults are neutral (no homelab literals).
- **Invalid-input → structured reason** (`GeminiValidationTests`): an `InlineData`
  matrix over every `Validate*` — required/length/control-char/leading-`-`/enum/
  path-traversal — including NUL inputs written with the C# escape `\0`.
- **Error-scrub contract** (`GeminiErrorsTests`): no private-key/credential leak
  (incl. `NANOBANANA_API_KEY`) and length-capping, plus the tail-and-scrub helper.
- **The load-bearing leg** (`SubprocessToolErrorTests`, mirroring StepCa's): tool
  guards short-circuit **before any spawn / filesystem access** (backend pointed at a
  nonexistent bundle / a traversal id), and — driving the real `GeminiCli` through a
  tiny `node` stub — a failing backend that **emits a secret on stderr** yields
  `[redacted]` in the tool error (and in a `research` task file), and the **timeout
  path actually fires** (a never-exiting stub is killed and flagged `TimedOut`). The
  node-driven legs no-op gracefully when node is absent (e.g. a CI SDK image); the
  short-circuit legs run everywhere.
