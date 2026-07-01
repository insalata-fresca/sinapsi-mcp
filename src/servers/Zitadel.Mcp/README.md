# Zitadel.Mcp

A personal-lab MCP **server** for a [ZITADEL](https://zitadel.com) identity instance. It is a
thin, hardened host over the ZITADEL management REST API: it wires a `ZitadelClient` over a
typed `HttpClient` (bearer token held server-side, hard per-request timeout), validates every
tool parameter before any call, scrubs upstream errors of credentials, and exposes a 15-tool
identity-management surface over streamable HTTP at `/mcp`. Reads are free; mutating tools are
flagged `Destructive` so a fronting policy plane can gate them.

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-15)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The server holds **no instance topology in source**. The instance root, service token, listen
port, HTTP timeout, and agent-key directory are all supplied by environment variables at deploy
time, so the binary carries no site- or deployment-specific wiring — point the same binary at
any ZITADEL deployment by setting `ZITADEL_BASE_URL` and a service token.

Architecturally it is these small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `ZitadelConfig.cs` | Bind + validate env into an immutable record; fail-closed on a missing token/base-URL and on a non-numeric / out-of-range port or HTTP timeout. |
| Validation | `ZitadelValidation.cs` | One `Validate*` method per parameter shape (id, name, description, enum, URI list, expiration, limit, agent-file basename); returns `string?` (null = ok), never throws. |
| Error scrub | `ZitadelErrors.cs` | `Sanitize(string)` — redact private-key blocks + `password/secret/client_secret/token/api-key/bearer/authorization` assignments, length-cap. |
| HTTP client | `Api/ZitadelClient.cs` | Shape the management-API paths; surface a non-2xx response as a `ZitadelApiException` carrying status + (truncated) body. |
| Tools | `Tools/*.cs` | The 15 MCP tools. Validate input first, then run the body through `ZitadelToolGuard` (which maps any upstream failure to a sanitized `{ ok:false, status, error }`). |

## Tool surface (15)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `list_users` | no | List users in the instance (first page). |
| `get_user` | no | Get a single user by id. |
| `list_projects` | no | List projects (first page). |
| `list_oidc_apps` | no | List the applications registered under a project (first page). |
| `get_oidc_app` | no | Get a single application within a project. |
| `create_project` | **yes** | Create a new project. |
| `create_oidc_app` | **yes** | Create an OIDC application (= client). |
| `update_oidc_app_config` | **yes** (Destructive) | Update an OIDC app's config (only provided fields sent). |
| `delete_oidc_app` | **yes** (Destructive) | Delete an application by id (irreversible). |
| `regenerate_oidc_secret` | **yes** (Destructive) | Rotate the OIDC client secret (SENSITIVE — response carries the new secret). |
| `create_machine_user` | **yes** | Create a machine (service) user. |
| `update_machine_user` | **yes** (Destructive) | Update a machine user (name / description / access-token type). |
| `delete_machine_user` | **yes** (Destructive) | Delete a user by id (RemoveUser; irreversible). |
| `create_pat` | **yes** | Issue a Personal Access Token for a machine user (SENSITIVE). |
| `create_machine_key` | **yes** | Issue a machine user's JSON key, written host-side to `AGENT_KEY_DIR` (the key is never returned). |

The machine-identity tools (`create_machine_user` / `create_machine_key` / `create_pat`) back
machine-to-machine (M2M) credential provisioning — mint a service identity + its credential.

## Per-tool reference

### `list_users`
- **Params:** `limit` (int, default 100) — must be `1..1000`.
- **Returns:** the ZITADEL user-search page (`{ result, details }`).
- **Errors:** an out-of-range `limit` → `{ ok:false, error }` before any call.

### `get_user`
- **Params:** `userId` (string, **required**) — rejected if empty, over 128 chars, containing control chars, or containing a path separator.
- **Returns:** the user object.
- **Errors:** `{ ok:false, error }` on invalid input (no call) or on an upstream failure (status + scrubbed message).

### `list_projects`
- **Params:** `limit` (int, default 100, `1..1000`).
- **Returns:** the project-search page.
- **Errors:** as `list_users`.

### `list_oidc_apps`
- **Params:** `projectId` (string, **required**, id-validated); `limit` (int, default 100, `1..1000`).
- **Returns:** the project's application-search page.
- **Errors:** invalid `projectId` / `limit` → `{ ok:false, error }` (no call).

### `get_oidc_app`
- **Params:** `projectId`, `appId` (both **required**, id-validated).
- **Returns:** the application object.
- **Errors:** invalid id → `{ ok:false, error }` (no call).

### `create_project` (mutates)
- **Params:** `name` (string, **required**) — rejected if empty, over 200 chars, or containing control chars.
- **Returns:** `{ id, details }`.
- **Errors:** invalid `name` → `{ ok:false, error }` (no call); upstream failure → sanitized envelope.

### `create_oidc_app` (mutates)
- **Params:** `projectId` (id), `name` (name), `redirectUris` (**required**, non-empty, ≤ 100 URIs, each ≤ 2048 chars, control-char-free), `postLogoutRedirectUris` (optional list), `responseTypes` / `grantTypes` (optional enum lists), `appType` / `authMethodType` / `accessTokenType` (enum tokens), `devMode` (bool).
- **Returns:** `{ appId, clientId, clientSecret?, details }`.
- **Errors:** any param failing validation → `{ ok:false, error }` before any call.

### `update_oidc_app_config` (mutates, Destructive)
- **Params:** `projectId`, `appId` (ids); every config field is **optional** — omit to leave unchanged. Provided fields are validated (URI lists, enum tokens) exactly as on create.
- **Returns:** the update result / details.
- **Errors:** invalid id or provided field → `{ ok:false, error }` (no call).

### `delete_oidc_app` (mutates, Destructive)
- **Params:** `projectId`, `appId` (ids). **Irreversible.**
- **Returns:** the delete result.
- **Errors:** invalid id → `{ ok:false, error }` (no call).

### `regenerate_oidc_secret` (mutates, Destructive, SENSITIVE)
- **Params:** `projectId`, `appId` (ids).
- **Returns:** the response carrying the **new client secret** — treat the result as a secret.
- **Errors:** invalid id → `{ ok:false, error }` (no call); an upstream error body is scrubbed of any credential before it is surfaced.

### `create_machine_user` (mutates)
- **Params:** `username` (name, **required**), `name` (defaults to `username`), `description` (optional, ≤ 500 chars), `accessTokenType` (enum, default `ACCESS_TOKEN_TYPE_JWT`).
- **Returns:** `{ userId, details }`.
- **Errors:** invalid param → `{ ok:false, error }` (no call).

### `update_machine_user` (mutates, Destructive)
- **Params:** `userId` (id), `name` (**required** — ZITADEL's UpdateMachine requires it; pass the existing name to leave unchanged), `description` (optional), `accessTokenType` (enum).
- **Returns:** the update result.
- **Errors:** invalid param → `{ ok:false, error }` (no call).

### `delete_machine_user` (mutates, Destructive)
- **Params:** `userId` (id). **Irreversible** — user, login names, keys, PATs and grants are removed.
- **Returns:** the RemoveUser result.
- **Errors:** invalid id → `{ ok:false, error }` (no call).

### `create_pat` (mutates, SENSITIVE)
- **Params:** `userId` (id), `expirationIso` (ISO-8601, default `2099-01-01T00:00:00Z`) — validated as a real timestamp, ≤ 64 chars, control-char-free.
- **Returns:** `{ tokenId, token, details }` — the `token` is a long-lived bearer credential; treat the result as a secret.
- **Errors:** invalid param → `{ ok:false, error }` (no call).

### `create_machine_key` (mutates)
- **Params:** `userId` (id), `agentFile` (bare basename — no `/`, `\` or `..`, ≤ 128 chars), `expirationIso` (ISO-8601, validated). Writes `AGENT_KEY_DIR/<agentFile>.json` (mode 0640).
- **Returns:** **only** `{ ok, userId, keyId, path, bytes }` — the private key is **never** returned or logged, so it never enters an agent transcript.
- **Errors:** invalid param → `{ ok:false, error }` before any call or disk write; an upstream failure returns a **sanitized** error and the key file is not written.

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `ZITADEL_BASE_URL` | yes | — | Instance root, e.g. `https://auth.example.com`. The `/management/v1/` API paths are appended. Server **fails to start** if unset. |
| `ZITADEL_TOKEN` | yes | — | A service-account / PAT bearer token, held server-side. Inject at deploy; **never bake it in**. Server fails to start if unset. |
| `ZITADEL_HTTP_TIMEOUT_MS` | no | `30000` | Hard ceiling on a single upstream HTTP call. Must be an integer in `1..600000` ms; a non-numeric, `<= 0`, or out-of-range value **fails startup** (rather than making every call time out or throwing deep in the client). |
| `AGENT_KEY_DIR` | no | `/agent-keys` | Host-side directory `create_machine_key` writes a machine user's JSON private key into (mode 0640). The key is never returned to the caller. |
| `ZITADEL_MCP_PORT` | no | `9220` | Listen port. A non-numeric / out-of-range value **fails startup** (previously silently ignored). |
| `ZITADEL_MCP_HOST` | no | `0.0.0.0` | Listen address. |

## Run

```sh
ZITADEL_BASE_URL=https://auth.example.com \
ZITADEL_TOKEN=<service-account bearer token> \
dotnet run -c Release --project src/servers/Zitadel.Mcp
# → MCP endpoint on http://0.0.0.0:9220/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped so
it cannot 400 an otherwise-valid request.

## Security notes

This server can mint machine identities, PATs, OIDC client secrets and JSON private keys. It is
built to fail safe:

- **Fail-closed config.** `ZITADEL_BASE_URL` and `ZITADEL_TOKEN` are required; the server throws
  on startup if either is missing, rather than running against an unintended default. A
  non-numeric / out-of-range `ZITADEL_MCP_PORT` or `ZITADEL_HTTP_TIMEOUT_MS` also fails startup
  (naming the offending var) rather than being silently swallowed.
- **No secrets in source.** The service token lives in `ZITADEL_TOKEN`, injected at deploy. It is
  held server-side on the typed `HttpClient` and never read into a tool response.
- **No secret leakage in errors.** Every surfaced upstream string is passed through
  `ZitadelErrors.Sanitize` before it leaves the process — uniformly across all tools, **reads
  included**, not just the mutating ones. PEM **private-key** blocks and
  `password=/secret=/client_secret=/token=/api-key=/bearer/authorization` style assignments are
  redacted (key name kept, value `[redacted]`), and the message is length-capped. A credential
  that somehow reached ZITADEL's error body cannot reach a caller. The `create_machine_key`
  hand-rolled catch routes through the same scrub.
- **Input validation before side effects.** Every tool validates its parameters
  (`ZitadelValidation`) — ids (length + control chars + no path separator), names, descriptions,
  enum tokens, URI lists (count + length + control chars), ISO-8601 expirations, paging limits,
  and the machine-key agent-file basename (no `/`, `\` or `..`) — **before** any HTTP call or
  disk write. Invalid input returns a structured error, never an exception.
- **Bounded HTTP.** Every upstream call runs under a hard timeout (`ZITADEL_HTTP_TIMEOUT_MS`,
  default 30 s) so a hung ZITADEL cannot wedge a tool call.
- **Path-traversal guard.** `create_machine_key` writes only to a bare basename under
  `AGENT_KEY_DIR`; a `../…` or separator-bearing name is rejected before any write, and the key
  file is written mode 0640.
- **Never-return-the-key.** `create_machine_key` returns only metadata (`{ok, userId, keyId,
  path, bytes}`); the private-key bytes are never serialised back to the caller.

## Error contract

Every tool returns a JSON object. On error it returns `{ "ok": false, "error": "…" }` (with a
`status` field carrying the upstream HTTP status when the failure was an upstream response). All
upstream error text is scrubbed of key/credential material and length-capped before being
returned. A successful call returns its normal ZITADEL payload unchanged. There are no
asymmetric error shapes — every tool uses the `{ ok:false, … }` envelope.

## Testing

```sh
dotnet test test/Zitadel.Mcp.Tests
```

The suite covers the tool-surface parity guard (all 15 tools, names + read/mutate
classification), the HTTP client's path/method shaping, config binding, and the **hardening
paths**:

- **Config fail-closed** (`ZitadelConfigTests`): required base-URL + token throw with a neutral
  example; the port and HTTP-timeout defaults + overrides; and a non-numeric / `<= 0` /
  out-of-range port or timeout is rejected **naming the offending var**.
- **Input validation** (`ZitadelValidationTests`): an InlineData matrix over every `Validate*`
  method — valid input returns `null`; each rejection reason (required, too-long, control chars,
  path separator, out-of-range, bad basename, unparseable expiration) is produced.
- **Error scrub contract** (`ZitadelErrorsTests`): private-key blocks and
  `password/client_secret/token/api-key/bearer/authorization` assignments are redacted, the
  message is length-capped, and a non-sensitive diagnostic is preserved.
- **Upstream/CLI-failure → sanitized error, end-to-end** (`ZitadelToolGuardTests`): each tool is
  driven with a malformed parameter through a handler that **fails the test if any HTTP request
  is issued** — proving validation short-circuits before the call (the analogue of pointing a
  binary at a nonexistent path). Then a **scripted handler** returns a non-2xx body carrying a
  fake secret, and the tool is asserted to surface `[redacted]` (not the raw secret) with the
  real status — the load-bearing "the scrub really fires at the tool level" leg, including the
  `create_machine_key` path (which also asserts no key file is written on failure).
