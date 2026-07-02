# Contributing

This is a personal research lab, so contribution is mostly a note-to-self.

- Each experiment lives in its own folder under `src/servers/` (an MCP server)
  or `src/libs/` (a shared library) and should build on its own.
- Target **.NET 8**. Run `dotnet build` (and `dotnet test` where there are
  tests) before opening a pull request.
- Keep secrets and any host-specific details out of the tree — see
  `.gitleaks.toml` and `.gitignore`.

If you found your way here from the outside: feel free to read and learn from
anything. The code is offered as-is under `LICENSE`.
