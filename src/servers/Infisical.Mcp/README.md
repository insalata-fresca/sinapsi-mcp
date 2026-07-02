# Infisical.Mcp

A personal-lab MCP **server** for issuing and storing secrets in an
[Infisical](https://infisical.com) project. It is a thin, hardened host over the
Infisical REST API and the shared [`Sinapsi.Mcp`](../../libs/Sinapsi.Mcp) hosting helpers,
exposing a 4-tool surface over streamable HTTP at `/mcp`. The point of this server is
**transcript-safety**: for generated material the secret *value* is produced
**server-side** and only *non-secret* material — a public key, a path, a name — is ever
returned to the caller.

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-4)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The server holds **no Infisical topology in source**. The host URL, machine-identity
credentials, project id and environment slug are all supplied by environment variables at
deploy time, so the binary carries no site- or deployment-specific wiring. Secrets are
organised under a two-level path: a **group** folder and a **service** folder beneath it,
e.g. `/<group>/<service>/<name>`.

Architecturally it is a few small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `InfisicalOptions.cs` | Bind + validate env into an immutable record; fail-closed when required config is missing; clamp the HTTP timeout. |
| REST client | `InfisicalClient.cs` | Universal-Auth login → token (cached) → folder / secret upsert / list. Secret values only transit here (generate → store); never logged. |
| Validation | `InfisicalValidation.cs` | One `Validate*` per parameter; rejects malformed input **before** any REST call (never throws). |
| Errors | `InfisicalErrors.cs` | `Sanitize` — redacts key material / credentials and length-caps any surfaced upstream string. |
| Tools | `InfisicalTools.cs` | The 4 MCP tools. Validates input, calls the client, scrubs upstream errors. |

## Tool surface (4)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `issue_nats_nkey` | **yes** | Generates a NATS user nkey **server-side**, stores the seed at `/<group>/<service>/NATS_NKEY_SEED`; returns only the public key (`U…`) + path. The seed never leaves the MCP. |
| `issue_random_secret` | **yes** | Generates a random hex secret **server-side** (default 32 bytes), stores it at `/<group>/<service>/<name>`; returns a confirmation (path + byte count) only. |
| `set_secret` | **yes** | Stores a **caller-supplied** value (e.g. a vendor-issued token) at `/<group>/<service>/<name>`. The value passes through the caller — prefer the generators above. |
| `list_secrets` | no | Lists secret **names** (never values) at `/<group>/<service>`. |

## Per-tool reference

### `issue_nats_nkey` (mutates)
- **Params:**
  - `group` (string, **required**) — folder slug. Rejected if empty/whitespace, longer than 128 chars, containing control characters or a path separator, or starting with `-`.
  - `service` (string, **required**) — validated like `group`.
- **Returns:** `{ public_key: "U…", path: "/<group>/<service>/NATS_NKEY_SEED", env }`.
- **Errors:** input-validation failures return `{ ok: false, error }` **before any REST call**. An upstream failure returns `{ ok: false, error }` with the message scrubbed of any key/credential material. The generated **seed** is never returned and never appears in an error.

### `issue_random_secret` (mutates)
- **Params:**
  - `group`, `service` (string, **required**) — validated as above.
  - `name` (string, **required**) — secret key. Rejected if empty/whitespace, longer than 256 chars, containing control characters or a path separator, or starting with `-`.
  - `bytes` (int) — random-secret length. A non-positive value defaults to **32**; a value above 4096 is rejected.
- **Returns:** `{ stored: "/<group>/<service>/<name>", bytes }`.
- **Errors:** validation failures and upstream failures both return `{ ok: false, error }` (scrubbed); no REST call is made on a validation failure. The generated value is never returned.

### `set_secret` (mutates)
- **Params:**
  - `group`, `service`, `name` (string, **required**) — validated as above.
  - `value` (string, **required**) — the caller-supplied secret. Free-form; rejected only if empty or longer than 64 KiB.
- **Returns:** `{ stored: "/<group>/<service>/<name>" }`.
- **Errors:** `{ ok: false, error }` on a validation or upstream failure (scrubbed). **Note:** this is the one tool where a secret transits the caller — prefer the generators.

### `list_secrets`
- **Params:** `group`, `service` (string, **required**) — validated as above.
- **Returns:** `{ path: "/<group>/<service>", names: [ … ] }` — **names only**, never values.
- **Errors:** `{ ok: false, error }` on a validation or upstream failure (scrubbed).

## Configuration

All configuration is read from the environment. The Universal-Auth client id/secret are
the MCP's **own** machine identity — inject them at deploy (e.g. via an env file) and
never bake them into the image.

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `INFISICAL_HOST_URL` | **yes** | — | Infisical root URL, e.g. `https://secrets.example.org`. The `/api` suffix is appended; a trailing slash is trimmed. **No host is baked in — the server fails to start if unset.** |
| `INFISICAL_UNIVERSAL_AUTH_CLIENT_ID` | **yes** | — | Universal-Auth machine-identity client id. Server **fails to start** if unset (rather than deferring an opaque 401 to the first login). |
| `INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET` | **yes** | — | Universal-Auth machine-identity client secret. Fails startup if unset. |
| `INFISICAL_PROJECT_ID` | **yes** | — | The Infisical project (workspace) id to write into. Fails startup if unset. |
| `INFISICAL_ENV` | no | `dev` | The Infisical environment slug (e.g. `dev`, `staging`, `prod`). |
| `INFISICAL_HTTP_TIMEOUT_MS` | no | `30000` | Hard ceiling on every Infisical REST call. Must be an integer in `1..600000` ms; a non-numeric, `<= 0`, or out-of-range value **fails startup** rather than silently making every call time out. |
| `INFISICAL_MCP_PORT` | no | `9215` | Listen port. |
| `INFISICAL_MCP_HOST` | no | `0.0.0.0` | Listen address. |

## Run

```sh
INFISICAL_HOST_URL=https://secrets.example.org \
INFISICAL_UNIVERSAL_AUTH_CLIENT_ID=<machine-identity client id> \
INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET=<machine-identity client secret> \
INFISICAL_PROJECT_ID=<infisical project id> \
INFISICAL_ENV=dev \
dotnet run -c Release --project src/servers/Infisical.Mcp
# → MCP endpoint on http://0.0.0.0:9215/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is
stripped so it cannot 400 an otherwise-valid request.

## Security notes

This server can mint and store real secrets in your Infisical project. It is built to
fail safe:

- **Transcript-safe by design.** Generated material (nkey seed, random secret) is produced
  server-side and stored; only a public key / path / byte-count is ever returned. The seed
  and the random bytes never appear in a tool result.
- **Fail-closed config.** `INFISICAL_HOST_URL`, the machine-identity client id/secret, and
  the project id are all **required**; the server throws on startup (naming the offending
  var) if any is missing, rather than binding an empty string and failing opaquely on the
  first login.
- **No secrets in source.** The machine identity lives in the deploy env, injected at run
  time. It is never read into a tool response and never logged.
- **No secret leakage in errors.** Every surfaced upstream string is passed through
  `InfisicalErrors.Sanitize` before it leaves the process — uniformly across **all four
  tools**, including `list_secrets`, not just the mutating ones. PEM **private-key** blocks
  and `password=/token=/secret=/secretValue:/Authorization:`-style assignments (shell- and
  JSON-form) are redacted, and the message is length-capped.
- **Input validation before side effects.** Every tool validates its parameters
  (group/service/name format + length + path-separator + leading-dash; value length; byte
  count) **before** any REST call. Invalid input returns a structured error, never an
  exception, and never reaches the API.
- **Bounded upstream calls.** Every Infisical REST call runs under a hard timeout
  (`INFISICAL_HTTP_TIMEOUT_MS`, default 30 s) so a hung upstream cannot wedge a tool call.

## Error contract

Every tool returns a JSON string. On error it returns `{ "ok": false, "error": "…" }`.
On success it returns the tool's documented payload (e.g. `{ "stored": "…" }`) — the
success shapes carry **no `ok` field** (unchanged for parity). All upstream error text is
scrubbed of key/credential material and length-capped before being returned.

## Testing

```sh
dotnet test test/Infisical.Mcp.Tests
```

The suite covers the tool-surface parity guard (exactly the 4 tools, each with a
description), the happy-path tool behaviour against a recording fake of the Infisical REST
API (transcript-safety: the value is stored but never returned), the REST client's
error/token branches (POST→PATCH upsert fallback, double-failure throw, token caching,
missing-`accessToken` guard), and the **hardening paths**:

- **Config binding** — every required var fails closed (missing → throws naming the var),
  defaults, trailing-slash trim, and the HTTP-timeout clamp (default / override /
  `<= 0` / non-numeric / out-of-range → throws).
- **Input validation** — a pure `Validate*` matrix, plus tool-guard tests that point the
  HTTP backend at a handler that throws if reached, proving validation short-circuits
  **before** any REST call.
- **Error-scrub contract** — no key/credential leaks (PEM blocks + shell/JSON credential
  assignments), length cap.
- **Upstream failure → sanitized error, end-to-end** (`ToolUpstreamErrorTests`): each tool
  is driven through a fake HTTP backend that emits a secret in its failure, asserting the
  tool returns `{ ok: false, error: <sanitized> }` with the secret replaced by `[redacted]`
  (never the raw value), and that the **timeout path actually fires** (a 50 ms client
  timeout against a stalling backend returns a structured error).
```
