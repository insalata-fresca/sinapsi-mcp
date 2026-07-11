# SageCouncil.Mcp

A personal-lab MCP **server** that convenes a small multi-AI "council" on a hard,
articulated question — a real design, architecture, trade-off, or second-opinion
problem you cannot confidently resolve alone. It fans the same prompt out, in
parallel, to three research members (each behind its own restricted machine
identity), caps each by a wall-clock deadline, then runs a synthesis pass that
reconciles the perspectives into one grounded answer. A consult is intentionally
long-running (members do genuine multi-step research), so it is **asynchronous**:
`consult` returns a `job_id` immediately and you collect the result later with
`consult_result`. It is a thin HTTP host over two in-repo libraries, hardened for
fail-closed config, uniform input validation, and secret-free error surfaces.

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-2)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The server holds **no infrastructure topology in source**. The agent backend, MCP
gateway, model, per-member identities, persona overlay and timeouts are all
supplied by environment variables at deploy time, so the binary carries no site-
or deployment-specific wiring; defaults are neutral (`127.0.0.1`, `example.com`).

Architecturally it is a few small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `CouncilService.cs` (`CouncilOptions`) | Bind + validate env into an immutable record; fail-closed on an invalid timeout. |
| Validation | `CouncilValidation.cs` | Fail-fast input guard: one `Validate*` per tool param, returns `string?` (null = ok), never throws. |
| Errors | `CouncilErrors.cs` | `Sanitize`: redact key material / credentials + length-cap every surfaced upstream string. |
| Tools | `ConsultTool.cs` | The 2 MCP tools; validate input first, dispatch/poll the job, return the JSON envelope. |
| Orchestration | `CouncilService.cs` | Parallel member fan-out (claude + gemini via the agent backend; chatgpt via the MCP gateway) + synthesis. |
| Job store | `ConsultJob.cs` | Detached background job + in-memory registry (1h retention); guarded snapshot for the poll. |

The **claude** member runs against an HTTP agent backend (`POST /v1/sessions`,
`POST .../messages`, `DELETE ...`). The **gemini** member runs the **`agy`** engine
(Antigravity CLI) through the SAME agent backend: it creates a HEADLESS session
(`engine=agy`, which the backend treats as the autonomous governance class),
injects the prompt via the non-blocking `POST .../prompt` lane (agy runs the turn
synchronously — one short-lived `agy --print` per inject), then reads the reply off
the `.../events` SSE transcript (assistant text blocks). The **chatgpt** member calls
`codex_codex` through the MCP gateway in a read-only sandbox. Each member
authenticates with its own RFC 7523 JWT-bearer identity minted by the in-repo
[`Sinapsi.AgentJwt`](../../libs/Sinapsi.AgentJwt) library (not a vendored copy), and
the gateway call goes through the in-repo
[`Sinapsi.Mcp`](../../libs/Sinapsi.Mcp) `GatewayMcpClient` — both by
`ProjectReference`, so the server builds with only nuget.org.

## Tool surface (2)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `consult` | no* | Dispatch the council on a `prompt` with a `focus` persona + optional `members` roster. Returns a `job_id` (status `running`). |
| `consult_result` | no | Poll a `job_id`: `running`, `done` (full members + synthesis), `error`, or `not_found`. Results retained ~1 hour. |

\* `consult` performs no local mutation; it dispatches a background job that makes
outbound research calls to the configured member backends. It is idempotent from
the server's perspective (each call starts a fresh job).

## Per-tool reference

### `consult`
- **Params:**
  - `prompt` (string, **required**) — the hard question + context. Rejected if empty/whitespace, longer than 100000 chars, or containing non-whitespace control characters (newlines/tabs are allowed — it is multi-line free text).
  - `focus` (string, default `general`) — persona/research mandate: `general | code-review | architecture | second-opinion | deep-research | design`, or any `<name>.md` file dropped in `PERSONA_DIR`. Rejected if empty, longer than 128 chars, containing control characters/newlines, or starting with `-`.
  - `members` (string[], optional) — roster: `claude-research | gemini-research | chatgpt-research`. Default: all three. Each entry is validated like `focus`; at most 32 entries.
- **Returns:** `{ job_id, status: "running", focus, members, started_at, note }`.
- **Errors:** input-validation failures return `{ ok: false, error }` **before any job is dispatched** (no background task starts).

### `consult_result`
- **Params:** `job_id` (string, **required**) — the id returned by a prior `consult`. Rejected if empty, longer than 128 chars, or containing control characters/newlines.
- **Returns (running):** `{ job_id, status: "running", focus, members, started_at, completed_at: null, elapsed_ms, result: null, error: null }`.
- **Returns (done):** the same envelope with `status: "done"` and `result` carrying the full council JSON (`{ prompt, focus, members: [{ member, report, latency_ms, error }], synthesis }`).
- **Returns (error):** `status: "error"` with a (sanitized) `error` and `result: null`.
- **Errors:** a malformed `job_id` returns `{ ok: false, error }` **before the store lookup**. A well-formed but unknown/expired id returns `{ job_id, status: "not_found", error }`.

## Configuration

Everything is env-driven with neutral local defaults — point it at your own
infrastructure. No value is baked to any specific deployment.

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `AGENT_BACKEND_URL` | no | `http://127.0.0.1:8088` | Agent backend the `claude-research` + `gemini-research` (agy) members + the synthesis pass spawn sessions on. |
| `GATEWAY_URL` | no | `http://127.0.0.1:8443/mcp` | MCP gateway the `chatgpt-research` member calls through. |
| `AGENT_MODEL` | no | `claude-sonnet-4-6` | Model the `claude-research` session is created with. |
| `SAGE_TIMEOUT_MS` | no | `1800000` (30 min) | Per-outbound-call ceiling (a safety net for a hung backend). Must be an integer in `1..7200000` ms; a non-numeric, `<= 0`, or out-of-range value **fails startup**. |
| `SAGE_MEMBER_TIMEOUT_MS` | no | `1500000` (25 min) | Per-member wall-clock deadline. Same `1..7200000` ms validation; **fails startup** on an invalid value. |
| `COUNCIL_CLAUDE_AGENT` | no | `agent-council-claude` | Identity name (JWK filename) for the claude member. |
| `COUNCIL_GEMINI_AGENT` | no | `agent-council-gemini` | Identity name for the gemini member. |
| `COUNCIL_CHATGPT_AGENT` | no | `agent-council-chatgpt` | Identity name for the chatgpt member. |
| `PERSONA_DIR` | no | `/etc/sage-council-mcp/personas` | Optional overlay: drop a `<focus>.md` file to add/override a persona — no recompile. |
| `SAGE_COUNCIL_MCP_HOST` | no | `0.0.0.0` | Listen address. |
| `SAGE_COUNCIL_MCP_PORT` | no | `9212` | Listen port. |

The OIDC issuer + audience + key-dir + TTL knobs (`OIDC_ISSUER`,
`OIDC_AUDIENCE_PROJECT_ID`, `AGENT_KEY_DIR`, `JWT_TTL_MIN`) are read by
`Sinapsi.AgentJwt.AgentJwtOptions.FromEnvironment()`; see that library's README
for the JWK file shape and the token-exchange flow.

## Run

```sh
AGENT_BACKEND_URL=http://backend.example.com:8088 \
GATEWAY_URL=http://gateway.example.com:8443/mcp \
dotnet run -c Release --project src/servers/SageCouncil.Mcp
# → MCP endpoint on http://0.0.0.0:9212/mcp
```

## Security notes

This server dispatches long-running research fan-outs to external backends under
per-member machine identities. It is built to fail safe:

- **Fail-closed config.** `SAGE_TIMEOUT_MS` / `SAGE_MEMBER_TIMEOUT_MS` are
  validated at startup: a non-numeric, `<= 0`, or above-ceiling (`> 7200000` ms)
  value throws an `InvalidOperationException` naming the offending var, rather
  than silently swallowing a footgun default (a `0` would make every call time
  out instantly; a mistyped extra zero would defeat the safety net).
- **No secrets in source.** Per-member identities live as JWK files in
  `AGENT_KEY_DIR` (mounted read-only), minted at call time; no token or key is
  ever read into a tool response or logged.
- **Bounded outbound calls.** Every member call runs under the linked-CTS
  `SAGE_TIMEOUT_MS` ceiling, and each member is additionally capped by the
  `SAGE_MEMBER_TIMEOUT_MS` wall-clock deadline; the whole job has an overall
  ceiling. `HttpClient.Timeout` is deliberately set to infinite so those
  cancellation ceilings (not the fixed 100 s default) govern a legitimately-long
  research call — but no call can exceed the clamped `SAGE_TIMEOUT_MS` (≤ 2 h).
- **Input validation before side effects.** `consult` validates `prompt`,
  `focus`, and each `members` entry, and `consult_result` validates `job_id`,
  **before** a job is dispatched or the store is queried. Invalid input returns a
  structured `{ ok: false, error }`, never an exception. `focus`/`members`/`job_id`
  are identifier-like, so control characters, newlines, and a leading `-` are
  rejected; `prompt` is free text, so newlines/tabs are allowed but other control
  characters (incl. NUL) are rejected and the length is capped.
- **No secret leakage in errors.** Every surfaced upstream string — a member
  error (from an upstream exception or a gateway "tool error"), a synthesis
  failure, a gemini `failed` transition, the job-fail message, and the
  `not_found` text — passes through `CouncilErrors.Sanitize` before it leaves the
  process. PEM **private-key** blocks and `password=/token=/secret=/api-key=/`
  `bearer/Authorization:` style assignments are redacted, and the message is
  length-capped. A credential that somehow reached an upstream error cannot reach
  a caller.
- **No shell.** The server makes no subprocess calls; all work is HTTP over the
  in-repo `GatewayMcpClient` + `HttpClient`.

## Error contract

Every tool returns a JSON string. On an **input-validation** failure it returns
`{ "ok": false, "error": "…" }` (the reason is sanitized for uniformity). A
**well-formed but unknown** `job_id` returns `{ "job_id", "status": "not_found",
"error" }`. A completed-but-**failed** job surfaces `{ …, "status": "error",
"error", "result": null }`. All upstream error text is scrubbed of
key/credential material and length-capped before being returned.

## Testing

```sh
dotnet test test/SageCouncil.Mcp.Tests
```

The suite covers the tool-surface parity guard (`consult` + `consult_result`),
the job state machine + snapshot, the live orchestration paths (2-member prose
synthesis, single-usable-member passthrough, design-focus structured-JSON merge,
per-member deadline, gemini poll done/failed) driven by a fake
`HttpMessageHandler`, and the **hardening paths**:

- **Config fail-closed matrix** (`CouncilOptionsFailClosedTests`) — an
  `InlineData` matrix of non-numeric / `<= 0` / above-ceiling values for both
  timeout vars, asserting startup throws and names the offending var; plus the
  unset-default and ceiling-boundary legs.
- **Input validation → structured error** (`CouncilValidationTests` +
  `ConsultToolGuardTests`) — an `InlineData` matrix of blank/control-char/leading-
  dash/oversize inputs, and tool-guard tests proving `consult` returns the
  `{ ok: false, error }` envelope with **no `job_id`** (no job dispatched) and
  `consult_result` short-circuits **before the store lookup**.
- **Error-scrub contract** (`CouncilErrorsTests`) — PEM key blocks + credential
  assignments redacted, length-cap enforced, clean messages pass through.
- **The load-bearing leg** (`CouncilServiceErrorScrubTests`, mirroring StepCa's
  `SubprocessToolErrorTests`) — a fake gemini backend emits a **secret** in its
  `failed` error; the tool's surfaced member error is asserted to contain
  `[redacted]`, not the raw secret. A second leg proves the per-member deadline
  **timeout actually fires** (and its message is sanitized).
```
