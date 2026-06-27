# Infisical.Mcp

A personal-lab MCP **server** for issuing and storing secrets in an
[Infisical](https://infisical.com) project. It is a thin host over the Infisical REST
API and the shared [`Sinapsi.Mcp`](../../libs/Sinapsi.Mcp) hosting helpers, exposing its
tool surface over streamable HTTP at `/mcp`.

The point of this server is **transcript-safety**. For generated material the secret
*value* is produced **server-side** and only *non-secret* material — a public key, a
path, a name — is ever returned to the caller. The seed of a generated keypair, or the
bytes of a generated random secret, never leave the MCP and never appear in a tool
result.

Secrets are organised under a two-level path: a **group** folder and a **service**
folder beneath it, e.g. `/<group>/<service>/<name>`.

## Tools

| Tool | What it does | Returns |
|------|--------------|---------|
| `issue_nats_nkey` | Generates a NATS user nkey **server-side**, stores the seed at `/<group>/<service>/NATS_NKEY_SEED`. | The public key (`U…`) + the path only. The seed stays in the MCP. |
| `issue_random_secret` | Generates a random hex secret **server-side** (default 32 bytes), stores it at `/<group>/<service>/<name>`. | A confirmation (path + byte count) only. |
| `set_secret` | Stores a **caller-supplied** value (e.g. a vendor-issued token) at `/<group>/<service>/<name>`. The value passes through the caller — prefer the generators above. | The stored path. |
| `list_secrets` | Lists secret **names** (never values) at `/<group>/<service>`. | The path + the list of names. |

## Configuration

All configuration is read from the environment. The Universal-Auth client id/secret are
the MCP's **own** machine identity — inject them at deploy (e.g. via an env file) and
never bake them into the image.

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `INFISICAL_HOST_URL` | no | `https://infisical.example.com` | Infisical root URL. The `/api` suffix is appended. |
| `INFISICAL_UNIVERSAL_AUTH_CLIENT_ID` | yes | — | Universal-Auth machine-identity client id. |
| `INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET` | yes | — | Universal-Auth machine-identity client secret. |
| `INFISICAL_PROJECT_ID` | yes | — | The Infisical project (workspace) id to write into. |
| `INFISICAL_ENV` | no | `dev` | The Infisical environment slug (e.g. `dev`, `staging`, `prod`). |
| `INFISICAL_MCP_PORT` | no | `9215` | Listen port. |
| `INFISICAL_MCP_HOST` | no | `0.0.0.0` | Listen address. |

### Example

```sh
INFISICAL_HOST_URL=https://secrets.example.org
INFISICAL_UNIVERSAL_AUTH_CLIENT_ID=<machine-identity client id>
INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET=<machine-identity client secret>
INFISICAL_PROJECT_ID=<infisical project id>
INFISICAL_ENV=dev
```

## Run

```sh
dotnet run -c Release --project src/servers/Infisical.Mcp
# → MCP endpoint on http://0.0.0.0:9215/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is
stripped so it cannot 400 an otherwise-valid request.
