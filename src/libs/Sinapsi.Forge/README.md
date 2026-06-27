# Sinapsi.Forge

A personal-lab .NET library: the **provider-neutral git-forge tool surface** shared by the
forge MCP servers in this repo.

It contains:

- **`IForgeClient`** — a provider-neutral abstraction over git-forge operations (repos,
  contents, branches, commits, issues, pull requests, releases, search, orgs, notifications,
  webhooks, repository topics, CI actions, and Gitea time-tracking).
- **The common `[McpServerTool]` classes** (`Tools/*`) — one MCP tool surface with identical
  names and semantics across every forge. The host registers a concrete `IForgeClient` adapter
  in DI and the tools call through it.
- **`GiteaForgeClient`** (`Gitea/*`) — the Gitea-REST adapter, which drives **both Forgejo and
  Codeberg** (they share the Gitea API). Byte-safe `commit_files` (base64, atomic, multi-file),
  binary blob read, release-asset upload, workflow dispatch + run listing, and topic management
  are first-class.
- **DTOs** (`Model/ForgeModels.cs`) and **`ForgeToolGuard`**, which maps a real forge HTTP
  failure to a structured `{ ok = false, status, error }` payload instead of a generic invoke error.

The GitHub adapter (`GitHubForgeClient`) lives in the `Github.Mcp` server, not here, because it
maps the same `IForgeClient` onto the GitHub REST API.

## Consuming it

Inside this repo the servers reference this project directly (`ProjectReference`), so the
solution builds with no package feed. The project is also `IsPackable` (`PackageId` =
`Sinapsi.Forge`), so it can be published to a NuGet feed and consumed by external projects via
`PackageReference`.

## Build / test

```sh
dotnet restore
dotnet build  -c Release
dotnet test   -c Release
dotnet pack   -c Release src/libs/Sinapsi.Forge/Sinapsi.Forge.csproj
```

Target framework: `net8.0`. The only runtime dependency is `ModelContextProtocol.Core`
(the light MCP package that carries the `[McpServerTool]` attributes — no ASP.NET).
