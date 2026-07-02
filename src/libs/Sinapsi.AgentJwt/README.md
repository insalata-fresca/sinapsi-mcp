# Sinapsi.AgentJwt

A small, hardened .NET **library** that mints OIDC access tokens for a machine
("agent") identity using the RFC 7523 **JWT-bearer** grant, with a per-agent
in-memory token cache. It is consumed by services (via `PackageReference`), not
run as a server. Part of a personal research lab; offered as-is.

It implements the flow a generic OIDC provider expects for a service-account
key:

1. Load the agent's JWK from `<KeyDir>/<agent>.json` — a JSON document holding
   `keyId`, `userId`, an RSA private `key` (PEM), and `type`.
2. Sign an RS256 JWT **assertion** (`iss`/`sub` = the user id, `aud` = the
   issuer, short `exp`).
3. POST it to `<Issuer>/oauth/v2/token` with
   `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` and a
   project-audience `scope`, and return the resulting `access_token`.

There is no external JWT library and no shell-out — it runs in a plain .NET
container with `System.Security.Cryptography`.

## Contents

- [Overview](#overview)
- [Public API reference](#public-api-reference)
- [Configuration](#configuration)
- [Usage](#usage)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)
- [Building](#building)

## Overview

The library holds **no provider topology in source**: the issuer, audience
project id, key directory and TTL are all environment-driven with neutral
defaults, so the binary carries no deployment-specific wiring. It is three
small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `AgentJwtMinter.cs` (`AgentJwtOptions`) | Bind env into an immutable options object; fail-closed on a non-numeric / out-of-range `JWT_TTL_MIN`. |
| Validation | `AgentJwtValidation.cs` | Guard the public input (agent name) and fail-closed-validate the bound options at the mint seam. |
| Mint | `AgentJwtMinter.cs` (`AgentJwtMinter`) | Load the JWK, sign the RS256 assertion, exchange it for a token, cache per agent. All surfaced errors routed through `AgentJwtErrors.Sanitize`. |

The main public types are **`AgentJwtOptions`** (config) and
**`AgentJwtMinter`** (the minter). `AgentJwtValidation` and `AgentJwtErrors`
are public so a host can reuse the same input-validation and redaction contract.

## Public API reference

### `AgentJwtOptions`
Immutable env-driven config.

- **Properties:** `KeyDir`, `Issuer`, `AudienceProjectId`, `TtlMinutes`.
- **`FromEnvironment()`** — binds the four env vars below.
  - **Errors:** throws `InvalidOperationException` naming `JWT_TTL_MIN` if it is
    set to a non-numeric or out-of-range value (`2..1440` minutes). Unset →
    the 15-minute default; empty is treated as unset.
- **Constants:** `DefaultTtlMinutes` (15), `MinTtlMinutes` (2),
  `MaxTtlMinutes` (1440).

### `AgentJwtMinter(HttpClient http, AgentJwtOptions opt)`
The minter; intended for DI (`AddHttpClient<AgentJwtMinter>()`). Thread-safe (a
`SemaphoreSlim` serialises the cache + refresh path).

- **`Task<string> MintAsync(string agent, CancellationToken ct)`** — mint (or
  return a cached) access token for `agent`.
  - **Inputs:** `agent` is validated first — it becomes the filename component
    `<KeyDir>/<agent>.json`, so a blank / over-long name, a path separator,
    traversal (`.` / `..`), NUL, or a control character is rejected with an
    `ArgumentException` **before** any cache lookup or filesystem access.
  - **Errors:** `ArgumentException` (bad agent name, or fail-closed options —
    missing/non-URL `Issuer`, missing/over-long/control-char `AudienceProjectId`,
    empty `KeyDir`, out-of-range `TtlMinutes` — the message names the offending
    option / env var); `FileNotFoundException` (no JWK for the agent);
    `InvalidOperationException` (malformed JWK / signing key, provider HTTP
    error, or a response with no `access_token`). Every surfaced message is
    redacted + length-capped.

### `AgentJwtValidation`
- **`string? ValidateAgent(string?)`** — returns `null` when valid, else a
  human-readable reason.
- **`void ValidateOptions(AgentJwtOptions)`** — fail-closed; throws
  `ArgumentException` naming the offending option when required config is
  missing or malformed.

### `AgentJwtErrors`
- **`string Sanitize(string?)`** — redact key material / credentials and
  length-cap any string before it is surfaced to a caller. Never returns null.
- **Constant:** `MaxErrorLength` (2000).

## Configuration

`AgentJwtOptions.FromEnvironment()` reads:

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `AGENT_KEY_DIR` | no | `/etc/agent-jwt/keys` | Directory of per-agent `<agent>.json` JWK files (read-only). |
| `OIDC_ISSUER` | yes (at mint) | `https://oidc.example` | Issuer base URL (absolute http(s), no trailing slash). Token endpoint is `<issuer>/oauth/v2/token`. A missing / non-URL issuer fails the mint. |
| `OIDC_AUDIENCE_PROJECT_ID` | yes (at mint) | _(empty)_ | Project id woven into the audience-scope URN. Empty fails the mint. |
| `JWT_TTL_MIN` | no | `15` | Assertion + cache TTL in minutes. Must be an integer in `2..1440`; a non-numeric / out-of-range value **throws** naming the var (rather than silently defaulting a footgun). |

The audience-scope uses the public project-audience URN form
(`urn:zitadel:iam:org:project:id:<id>:aud`), an OSS protocol value. Point
`OIDC_ISSUER` / `OIDC_AUDIENCE_PROJECT_ID` at your own provider/project.

> Required-at-mint means the value is validated the first time a token is
> minted (not at construction), so DI wiring stays cheap; a footgun config
> surfaces as a clear `ArgumentException` on the first `MintAsync`.

## Usage

```csharp
using Sinapsi.AgentJwt;

builder.Services.AddSingleton(AgentJwtOptions.FromEnvironment());
builder.Services.AddHttpClient<AgentJwtMinter>();

// elsewhere
string token = await minter.MintAsync("my-agent", ct);
```

## Security notes

This library signs assertions with a real RSA private key and hands back real
access tokens. It is built to fail safe:

- **Fail-closed config.** `OIDC_ISSUER` and `OIDC_AUDIENCE_PROJECT_ID` are
  required at mint time; a missing value, a non-http(s) issuer, or an
  out-of-range TTL throws an `ArgumentException` naming the offending option,
  rather than minting against an unintended default.
- **No secrets in source.** The signing key lives in a per-agent JWK file in
  `AGENT_KEY_DIR` (mounted read-only); it is never read into a response or a
  log line.
- **No secret leakage in errors.** Every surfaced string is passed through
  `AgentJwtErrors.Sanitize` before it leaves the process. A PEM **private-key**
  block, a NATS **NKey/seed**, and `password=/secret=/token=/api-key=/bearer=/`
  `authorization:/nkey=/seed=/signing-key=` style assignments are redacted, and
  the message is length-capped. A provider error that echoed the assertion or a
  token, or a malformed signing key that made `ImportFromPem` quote the PEM,
  cannot reach a caller.
- **Input validation before side effects.** The `agent` name is validated
  **before** any cache lookup or filesystem access, so a name containing a path
  separator or `..` can never escape `AGENT_KEY_DIR`.
- **Bounded TTL.** The assertion + cache TTL is clamped to `2..1440` minutes; a
  value outside that range is a config error, not silently honoured.

## Error contract

`MintAsync` throws on failure — it never returns a partial or empty token. All
exception messages it raises are redacted (key/credential material →
`[redacted]`) and length-capped by `AgentJwtErrors.Sanitize`. The provider HTTP
status is preserved for diagnosability; the response body is sanitized before it
is appended.

## Testing

```sh
dotnet test test/Sinapsi.AgentJwt.Tests
```

The suite covers the end-to-end mint (assertion shape, form fields, RS256
signature verification, the per-agent cache, provider HTTP-error handling) and
the **hardening paths**:

- **Config fail-closed** (`AgentJwtOptionsTests`, `AgentJwtValidationTests`):
  a non-numeric / out-of-range `JWT_TTL_MIN` throws naming the var; missing /
  malformed required options throw naming the offending option.
- **Input validation** (`AgentJwtValidationTests`, `AgentJwtMinterHardeningTests`):
  the agent-name matrix (blank, over-long, path separator, traversal, NUL,
  control chars) is rejected — and rejected **before any filesystem or network
  I/O** when driven through `MintAsync`.
- **Error sanitization** (`AgentJwtErrorsTests`, `AgentJwtMinterHardeningTests`)
  — the load-bearing leg: a private-key block, an NKey/seed, or a
  bearer/token/secret assignment embedded in a provider error (or a malformed
  signing key) is `[redacted]` in the surfaced exception and length-capped; the
  signing key is never echoed.

## Building

Targets **.NET 8**.

```sh
dotnet build
dotnet test
```
