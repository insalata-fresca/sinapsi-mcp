# StepCa.Mcp

A personal-lab MCP **server** for a [`step-ca`](https://smallstep.com/docs/step-ca/)
internal certificate authority. It is a thin, hardened host over the
host-installed `step` CLI: it shells out to `step` for the CA-touching
operations, parses certificates with the .NET BCL, and exposes a 6-tool surface
over streamable HTTP at `/mcp`.

This server is the **reference-grade exemplar** for the toolkit — the security,
testing, documentation and craft bar that the other servers follow.

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-6)
- [Per-tool reference](#per-tool-reference)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Certificate-metadata notes](#notes-on-certificate-metadata)
- [Testing](#testing)

## Overview

The server holds **no certificate-authority topology in source**. The CA URL,
root-certificate path, issuer provisioner and password file are all supplied by
environment variables at deploy time, so the binary carries no site- or
deployment-specific wiring.

Architecturally it is three small seams:

| Seam | File | Responsibility |
|------|------|----------------|
| Config | `StepCaOptions.cs` | Bind + validate env into an immutable record; fail-closed when required config is missing. |
| Subprocess | `StepCli.cs` | Run the host `step` CLI with a hard timeout, drained pipes, and a kill-tree on timeout. |
| Tools | `StepCaTools.cs` | The 6 MCP tools. Validates input (`StepCaValidation`), parses certs with the BCL, scrubs upstream errors (`StepCaErrors`). |

## Tool surface (6)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `get_ca_health` | no | `step ca health` + `step version`; reports reachability and whether the root cert is present. |
| `get_root_certificate` | no | Reads the configured root cert from disk and returns its PEM + parsed metadata. |
| `list_provisioners` | no | `step ca provisioner list`; returns `{name, type}` for each provisioner. |
| `issue_certificate` | **yes** | `step ca certificate <CN> …` via the issuer JWK provisioner; returns the cert + private key PEM and metadata. |
| `revoke_certificate` | **yes** | `step ca revoke <serial>`; revokes a previously-issued certificate. |
| `inspect_certificate` | no | Parses a supplied PEM string (no subprocess) and returns subject/issuer/SANs/validity/fingerprint. PEM input capped at 64 KiB. |

## Per-tool reference

### `get_ca_health`
- **Params:** none.
- **Returns:** `{ ok, ca_url, step_health_output, step_health_error, step_cli_version, root_certificate_path, root_certificate_present }`.
  `ok` is `true` only when `step ca health` exits 0 **and** its stdout is exactly `ok` (exact match, not a substring).
- **Errors:** never throws; a non-reachable CA yields `ok: false` with the (scrubbed) `step_health_error`.

### `get_root_certificate`
- **Params:** none.
- **Returns:** `{ format: "pem", subject, issuer, serial_number, fingerprint_sha256, not_before, not_after, pem }`.
- **Errors:** a missing root file returns `{ error }` **without** an `ok` key
  (intentionally asymmetric, kept for back-compat). A present-but-malformed root
  file also returns `{ error }` rather than throwing.

### `list_provisioners`
- **Params:** none.
- **Returns:** `{ ok: true, provisioners: [{ name, type }], count }`.
- **Errors:** `{ ok: false, error }` on a non-zero `step` exit (error scrubbed),
  or `{ ok: false, error: "non-JSON response", raw }` if `step` output is not the
  expected JSON array (`raw` is truncated to 400 chars).

### `issue_certificate` (mutates)
- **Params:**
  - `common_name` (string, **required**) — the CN. Rejected if empty/whitespace, longer than 255 chars, containing control characters, or starting with `-`.
  - `sans` (string[], optional) — Subject Alternative Names. Each entry is validated like `common_name`; at most 100 SANs.
- **Returns:** `{ ok: true, common_name, subject, sans, serial_number, fingerprint_sha256, not_before, not_after, certificate_pem, private_key_pem }`.
- **Errors:** input-validation failures return `{ ok: false, error }` **before any subprocess is spawned**. A `step` failure returns `{ ok: false, error }` with the upstream message scrubbed of any key/credential material.
- **Note:** the cert + key are written to a per-call temp directory that is deleted in a `finally`; only the PEM strings are returned in the response.

### `revoke_certificate` (mutates)
- **Params:**
  - `serial_number` (string, **required**) — decimal or `0x`-hex non-negative integer. Empty/whitespace yields exactly `serial_number is required`; malformed/oversized serials are rejected.
  - `reason` (string, default `unspecified`) — free-text reason.
  - `reason_code` (int, default `0`) — RFC 5280 CRL reason code; must be `0–10`.
- **Returns:** `{ ok: true, serial_number, reason, reason_code, ca_response }`.
- **Errors:** validation failures and `step` failures both return `{ ok: false, error }` (scrubbed); no subprocess is spawned on a validation failure.

### `inspect_certificate`
- **Params:** `certificate_pem` (string, **required**) — a PEM certificate, capped at 64 KiB.
- **Returns:** `{ ok: true, subject, issuer, serial_number, subject_alt_names, not_before, not_after, seconds_until_expiry, expired, fingerprint_sha256, public_key_algorithm }`.
- **Errors:** oversize input → `{ ok: false, error: "…too large…" }`; an unparseable PEM → `{ ok: false, error: "could not parse PEM: …" }`. **No subprocess is involved** — this is pure BCL parsing.

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `STEP_CA_URL` | yes | — | CA URL, e.g. `https://ca.example.com:9000`. Passed via `--ca-url`. Server **fails to start** if unset. |
| `STEP_CA_ROOT_CERT` | no | `/etc/step-ca-mcp/root_ca.crt` | Root cert path. Passed via `--root` and read directly by `get_root_certificate`. |
| `STEP_CA_FINGERPRINT` | no | empty | Informational only; not used in calls. |
| `STEP_BIN` | no | `/usr/local/bin/step` | Path to the host `step` CLI binary. |
| `MCP_ISSUER_PROVISIONER` | no | `mcp-issuer` | JWK provisioner used by `issue_certificate` / `revoke_certificate`. |
| `MCP_ISSUER_PASSWORD_FILE` | no | `/etc/step-ca-mcp/mcp-issuer-password.txt` | Password file for the issuer provisioner. Inject at deploy; never bake it in. |
| `STEP_SUBPROCESS_TIMEOUT_MS` | no | `30000` | Hard ceiling on the entire `step` subprocess invocation. |
| `STEP_CA_HTTP_TIMEOUT_MS` | no | — | **Deprecated alias** for `STEP_SUBPROCESS_TIMEOUT_MS` (read only when the canonical var is unset). |
| `STEP_CA_MCP_HOST` | no | `0.0.0.0` | Listen address. |
| `STEP_CA_MCP_PORT` | no | `9109` | Listen port. |

## Run

```sh
STEP_CA_URL=https://ca.example.com:9000 \
STEP_CA_ROOT_CERT=/etc/step-ca-mcp/root_ca.crt \
dotnet run -c Release --project src/servers/StepCa.Mcp
# → MCP endpoint on http://0.0.0.0:9109/mcp
```

The `step` CLI must be installed on the host (or mounted into the container) at
`STEP_BIN`. The transport is stateless; a fronting proxy's forwarded
`Mcp-Session-Id` header is stripped so it cannot 400 an otherwise-valid request.

## Security notes

This server can mint and revoke real certificates from your CA. It is built to
fail safe:

- **Fail-closed config.** `STEP_CA_URL` is required; the server throws on
  startup if it is missing, rather than running against an unintended default.
- **No secrets in source.** The issuer password lives in a file
  (`MCP_ISSUER_PASSWORD_FILE`) injected at deploy. It is never read into a tool
  response and never logged.
- **No secret leakage in errors.** Every upstream `step` error is passed through
  `StepCaErrors.Sanitize` before it leaves the process: PEM **private-key**
  blocks and `password=/token=/secret=/Authorization:` style assignments are
  redacted, and the message is length-capped. A pasted key or password that
  somehow reached `step`'s stderr cannot reach a caller.
- **No shell.** Subprocess arguments are passed via
  `ProcessStartInfo.ArgumentList` (no shell, no string interpolation), so a
  hostile CN/SAN/serial cannot inject a shell command. As defence in depth,
  values starting with `-` are rejected so they can't be mistaken for a `step`
  flag.
- **Input validation before side effects.** `issue_certificate` and
  `revoke_certificate` validate every parameter (CN/SAN format + length + count,
  serial format + length, reason-code range) **before** any subprocess is
  spawned. Invalid input returns a structured error, never an exception.
- **Bounded subprocess.** Every `step` call runs under a hard timeout
  (`STEP_SUBPROCESS_TIMEOUT_MS`, default 30 s) with the process tree killed and
  awaited on timeout, so a hung `step` cannot wedge the server.
- **Bounded input.** `inspect_certificate` caps PEM input at 64 KiB to avoid a
  large-object-heap allocation on a pathological paste.
- **Deterministic cleanup.** Issued cert+key temp files are written to a
  per-call temp directory removed in a `finally`; `X509Certificate2` handles are
  disposed via `using`.

## Error contract

Every tool returns a JSON object. On error it returns
`{ "ok": false, "error": "…" }`, **except** `get_root_certificate`, whose error
paths return `{ "error": "…" }` without an `ok` key (kept asymmetric on purpose
for back-compat). All upstream-`step` error text is scrubbed of key/credential
material and length-capped before being returned.

## Notes on certificate metadata

Certificate parsing uses the .NET BCL (`System.Security.Cryptography.X509Certificates`):

- `subject` / `issuer` are the X.500 distinguished-name strings
  (`X500DistinguishedName.Name`).
- `serial_number` is the decimal form of the cert serial.
- `subject_alt_names` covers DNS and IP SAN entries.
- `not_before` / `not_after` are ISO-8601 UTC (`"O"` round-trip format).
- `inspect_certificate.seconds_until_expiry` is time-of-call dependent.

## Testing

```sh
dotnet test test/StepCa.Mcp.Tests
```

The suite covers the tool-surface parity guard, config binding (required +
defaults + the timeout alias), the serial→decimal conversion, the subprocess
timeout/kill path, BCL cert parsing (`get_root_certificate` / `inspect_certificate`),
and the **hardening paths**: input validation for both mutating tools (rejected
before any subprocess spawns) and the error-scrubbing contract (no key/credential
material, length-capped).

The **upstream/CLI-failure → structured-error** path is exercised end-to-end at
the tool level (`SubprocessToolErrorTests`): `list_provisioners`,
`issue_certificate` and `revoke_certificate` are driven through a stock binary
(`/bin/false`, `/bin/echo`, or a tiny purpose-built script) to force a non-zero
exit / malformed (non-JSON) stdout WITHOUT a live CA, asserting each returns the
`{ ok: false, error: <sanitized> }` envelope — including that a secret emitted on
the failing binary's stderr is redacted by `StepCaErrors.FromStepResult`.
`get_ca_health` is covered on both legs (a binary that prints `ok` → healthy, and
a non-`ok` stdout → not-healthy, proving the exact-match guard).
