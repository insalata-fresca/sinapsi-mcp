# TODO

A loose list of things to get to. No promises, no schedule.

- **Push-mirror to GitHub.** Hold a Forgejo → GitHub push-mirror to
  `github.com/insalata-fresca/sinapsi-mcp` for an eventual public, read-only
  copy. This stays parked until an operator deliberately flips it on; nothing
  here is public by default, and the CI does not push to any public package
  feed (nuget.org / GHCR) yet.

- **Release path.** Publish to the internal Forgejo NuGet feed first; gate any
  public feed (nuget.org) on a separate, operator-triggered decision.
