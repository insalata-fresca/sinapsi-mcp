# StepCa.Mcp

A personal-lab MCP **server** for a [`step-ca`](https://smallstep.com/docs/step-ca/)
internal certificate authority. It is a thin host over the host-installed `step`
CLI: it shells out to `step` for the CA-touching operations and parses
certificates with the .NET BCL, exposing the tool surface over streamable HTTP
at `/mcp`.

The server holds no certificate-authority topology in source — the CA URL, root
certificate path, issuer provisioner and password file are all supplied by
environment variables at deploy time.

## Tools (6)

| Tool | Mutates | What it does |
|------|:-------:|--------------|
| `get_ca_health` | no | `step ca health` + `step version`; reports reachability and whether the root cert is present. |
| `get_root_certificate` | no | Reads the configured root cert from disk and returns its PEM + parsed metadata. |
| `list_provisioners` | no | `step ca provisioner list`; returns `{name, type}` for each provisioner. |
| `issue_certificate` | **yes** | `step ca certificate <CN> …` via the issuer JWK provisioner; returns the cert + private key PEM and metadata. |
| `revoke_certificate` | **yes** | `step ca revoke <serial>`; revokes a previously-issued certificate. |
| `inspect_certificate` | no | Parses a supplied PEM string (no subprocess) and returns subject/issuer/SANs/validity/fingerprint. PEM input capped at 64 KiB. |

Each tool returns a JSON object; on error it returns `{ "ok": false, "error": "…" }`
(except `get_root_certificate`, whose missing-file path returns `{ "error": "…" }`
without an `ok` key — kept asymmetric on purpose).

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `STEP_CA_URL` | yes | — | CA URL, e.g. `https://ca.example.com:9000`. Passed via `--ca-url`. |
| `STEP_CA_ROOT_CERT` | no | `/etc/step-ca-mcp/root_ca.crt` | Root cert path. Passed via `--root` and read directly by `get_root_certificate`. |
| `STEP_CA_FINGERPRINT` | no | empty | Informational only; not used in calls. |
| `STEP_BIN` | no | `/usr/local/bin/step` | Path to the host `step` CLI binary. |
| `MCP_ISSUER_PROVISIONER` | no | `mcp-issuer` | JWK provisioner used by `issue_certificate` / `revoke_certificate`. |
| `MCP_ISSUER_PASSWORD_FILE` | no | `/etc/step-ca-mcp/mcp-issuer-password.txt` | Password file for the issuer provisioner. Inject at deploy; never bake it in. |
| `STEP_SUBPROCESS_TIMEOUT_MS` | no | `30000` | Bounds the entire `step` subprocess invocation. |
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

## Notes on certificate metadata

Certificate parsing uses the .NET BCL (`System.Security.Cryptography.X509Certificates`):

- `subject` / `issuer` are the X.500 distinguished-name strings
  (`X500DistinguishedName.Name`).
- `serial_number` is the decimal form of the cert serial.
- `subject_alt_names` covers DNS and IP SAN entries.
- `not_before` / `not_after` are ISO-8601 UTC (`"O"` round-trip format).
- `inspect_certificate.seconds_until_expiry` is time-of-call dependent.
