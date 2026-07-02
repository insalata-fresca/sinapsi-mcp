# TODO

A loose list of things to get to. No promises, no schedule.

- **Public copy = curated snapshot releases, not a live mirror.** The public,
  read-only copy at `github.com/insalata-fresca/sinapsi-mcp` is seeded from a
  clean-identity snapshot and updated only by deliberate, gated releases — never
  a continuous Forgejo→GitHub mirror of internal `main`. Each public update runs
  a full-history confidentiality scan and normalizes author identity before it
  is pushed. The development history stays on the private working repo.

- **Release path.** Publish to the internal Forgejo NuGet feed first; gate any
  public package feed (nuget.org / GHCR) on a separate, operator-triggered
  decision. Nothing is published to a public package feed yet.
