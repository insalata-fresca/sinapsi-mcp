# sinapsi-mcp

A personal research lab for exploring agentic-infrastructure patterns.

This repository is a workbench. It collects small, self-contained .NET
experiments — mostly [Model Context Protocol](https://modelcontextprotocol.io)
(MCP) servers and the shared libraries they lean on — that I write to learn how
language-model agents and the systems around them actually behave when you make
them do real work.

It is a sibling to my written notes and design essays: where those reason about
agentic software in prose, this one reasons about it in code you can compile and
run. There is no product here, no service to sign up for, and nothing to buy.
Just things I am curious about and want to try.

## What's inside

The layout is deliberately plain, so experiments can be added without ceremony:

```
src/
  libs/      shared .NET libraries (helpers reused across experiments)
  servers/   individual MCP servers, one per subfolder
Sinapsi.Mcp.sln   the solution that ties them together
```

Right now the tree is mostly empty — placeholders mark where things will land.
Each experiment is meant to stand on its own: read it, build it, throw it away.

## Building

Everything targets **.NET 8**.

```sh
dotnet restore
dotnet build
```

Shared libraries are packed and consumed as NuGet packages rather than wired
together with project references, so an experiment depends only on the published
version it asks for. The home lab serves these packages from its own Forgejo
NuGet feed; see `nuget.config` for the source list.

## Continuous integration

A small Forgejo Actions workflow (`.forgejo/workflows/ci.yml`) restores, builds
and tests on every pull request, and on a version tag (`v*`) packs the libraries
and pushes them to the lab's own Forgejo NuGet feed. That feed is internal — the
home lab consumes from it. Publishing anywhere public is intentionally left out
for now (see `TODO.md`).

## Notes

This is exploratory code written for my own learning. It changes when I learn
something, and it is offered as-is under the terms in `LICENSE`.
