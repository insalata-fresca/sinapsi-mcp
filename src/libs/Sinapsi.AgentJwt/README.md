# Sinapsi.AgentJwt

A small .NET helper that mints OIDC access tokens for a machine ("agent")
identity using the RFC 7523 **JWT-bearer** grant. Part of a personal research
lab; offered as-is.

It implements the flow an OIDC provider (e.g. Zitadel) expects for a
service-account key:

1. Load the agent's JWK from `<KeyDir>/<agent>.json` — a JSON document holding
   `keyId`, `userId`, an RSA private `key` (PEM), and `type`.
2. Sign an RS256 JWT **assertion** (`iss`/`sub` = the user id, `aud` = the
   issuer, short `exp`).
3. POST it to `<Issuer>/oauth/v2/token` with
   `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` and a
   project-audience `scope`, and return the resulting `access_token`.

A per-agent in-memory cache returns a still-fresh token without a round-trip;
the cache entry expires one minute before the token does, as a clock-skew
margin. The class is thread-safe (a `SemaphoreSlim` serialises cache + refresh).

There is no external JWT library and no shell-out — it runs in a plain .NET
container with `System.Security.Cryptography`.

## Usage

```csharp
using Sinapsi.AgentJwt;

builder.Services.AddSingleton(AgentJwtOptions.FromEnvironment());
builder.Services.AddHttpClient<AgentJwtMinter>();

// elsewhere
string token = await minter.MintAsync("my-agent", ct);
```

## Configuration

`AgentJwtOptions.FromEnvironment()` reads:

| Env var | Default | Meaning |
|---|---|---|
| `AGENT_KEY_DIR` | `/etc/agent-jwt/keys` | Directory of per-agent `<agent>.json` JWK files (read-only). |
| `OIDC_ISSUER` | `https://oidc.example` | Issuer base URL (no trailing slash). Token endpoint is `<issuer>/oauth/v2/token`. |
| `OIDC_AUDIENCE_PROJECT_ID` | _(empty)_ | Project id woven into the audience scope URN. |
| `JWT_TTL_MIN` | `15` | Assertion + cache TTL in minutes (positive only; else 15). |

The audience scope uses the public project-audience URN form
(`urn:zitadel:iam:org:project:id:<id>:aud`, e.g. Zitadel), an OSS protocol
value. Point `OIDC_ISSUER` / `OIDC_AUDIENCE_PROJECT_ID` at your own
provider/project.

## Building

Targets **.NET 8**.

```sh
dotnet build
dotnet test
```
