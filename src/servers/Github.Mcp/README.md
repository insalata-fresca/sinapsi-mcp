# Github.Mcp

A personal-lab MCP **server** for GitHub. It is a thin host over
[`Sinapsi.Forge`](../../libs/Sinapsi.Forge): the `GitHubForgeClient` adapter (raw GitHub
REST) implements the shared `IForgeClient`, so the **same tool surface** that drives Forgejo
and Codeberg also drives GitHub — identical tool names and semantics across forges.

The adapter handles the GitHub-specific divergences from the Gitea API:

- `commit_files` → the Git Data/Tree API (blobs → tree → commit → ref) for atomic, byte-safe
  multi-file commits;
- search → the `{ items: [...] }` wrapper;
- release-asset upload → the `uploads.github.com` host;
- time-tracking → unsupported (Gitea-only) ⇒ `ForgeNotSupportedException`.

## Configuration

| Env var                        | Required | Default   | Purpose |
|--------------------------------|:--------:|-----------|---------|
| `GITHUB_TOKEN`                 | yes\*    | —         | GitHub personal access token, held server-side. |
| `GITHUB_PERSONAL_ACCESS_TOKEN` | yes\*    | —         | Alternative name for the same token. |
| `GITHUB_MCP_PORT`              | no       | `9218`    | Listen port. |
| `GITHUB_MCP_HOST`              | no       | `0.0.0.0` | Listen address. |

\* One of `GITHUB_TOKEN` / `GITHUB_PERSONAL_ACCESS_TOKEN` is required. Inject it at deploy;
never bake it in.

## Run

```sh
GITHUB_TOKEN=<github pat> dotnet run -c Release --project src/servers/Github.Mcp
# → MCP endpoint on http://0.0.0.0:9218/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped
so it cannot 400 an otherwise-valid request.
