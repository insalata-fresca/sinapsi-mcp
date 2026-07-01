# Sinapsi.Forge

A personal-lab .NET library: the **provider-neutral git-forge tool surface** shared by the
forge MCP servers in this repo. One `[McpServerTool]` surface (identical tool names + semantics)
is registered by two thin hosts — `Forge.Mcp` (Gitea API → Forgejo / Gitea / Codeberg) and
`Github.Mcp` (GitHub REST) — each of which injects a concrete `IForgeClient` adapter.

This library follows the security / testing / documentation bar set by the reference-grade
exemplar `StepCa.Mcp`: input is validated before any HTTP call, every surfaced error string is
scrubbed of credential material, and the tool surface is covered by a fake-transport test suite.

## Contents

- [Overview](#overview)
- [Tool surface](#tool-surface-77)
- [Hardening seams](#hardening-seams)
- [Configuration](#configuration)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Consuming it](#consuming-it)
- [Testing](#testing)

## Overview

The library holds **no forge topology in source**. The base URL, token, and per-forge tool
selection are all supplied by the *host* from the environment at deploy time (see the host
READMEs). Architecturally it is a small number of seams:

| Seam | File(s) | Responsibility |
|------|---------|----------------|
| Abstraction | `IForgeClient.cs` | Provider-neutral git-forge operations + capability flags. |
| Adapter | `Gitea/GiteaForgeClient*.cs` | The Gitea-REST adapter — drives **Forgejo, Gitea, and Codeberg** (same API; only base URL + token differ). The GitHub adapter lives in the `Github.Mcp` server. |
| Tools | `Tools/*Tools.cs` | The 77 shared `[McpServerTool]` methods. Each validates input (`SinapsiForgeValidation`) at the top, calls the injected `IForgeClient`, and surfaces scrubbed errors via `ForgeToolGuard`. |
| Validation | `Tools/SinapsiForgeValidation.cs` | One `Validate*` helper per parameter shape; returns `string?` (null = ok), never throws. |
| Error scrubbing | `Tools/SinapsiForgeErrors.cs` | `Sanitize()` — redacts credential/key material + length-caps any surfaced upstream string. |
| Tool guard | `Tools/ForgeToolGuard.cs` | Validation-first wrapper → `{ ok:false, status, error }` envelope; routes every error through `Sanitize()`. |
| Client options | `ForgeClientOptions.cs` | Fail-closed HTTP-timeout binding shared by both hosts. |
| DTOs | `Model/ForgeModels.cs` | The provider-neutral request/response records. |

## Tool surface (77)

Identical tool **names** across all forges; the host chooses which classes to register.

| Group (class) | Tools |
|---------------|-------|
| `UserTools` | `get_me`, `get_user`, `search_users` |
| `RepoTools` | `get_repo`, `list_my_repos`, `search_repos`, `create_repo`, `fork_repo`, `edit_repo`, `delete_repo` |
| `ContentTools` | `get_file`, `get_file_binary`, `create_or_update_file`, `delete_file`, `commit_files` |
| `BranchTools` | `list_branches`, `get_branch`, `create_branch`, `delete_branch` |
| `CommitTools` | `list_commits`, `get_commit` |
| `IssueTools` | `create_issue`, `get_issue`, `list_issues`, `update_issue`, `list_issue_comments`, `create_issue_comment`, `edit_issue_comment`, `delete_issue_comment`, `list_repo_labels`, `add_issue_labels`, `remove_issue_label`, `list_milestones` |
| `PullRequestTools` | `create_pull_request`, `get_pull_request`, `list_pull_requests`, `update_pull_request`, `merge_pull_request`, `list_pull_request_files`, `get_pull_request_diff`, `list_pull_reviews`, `create_pull_review`, `request_reviewers` |
| `ReleaseTools` | `list_releases`, `get_latest_release`, `create_release`, `upload_release_asset`, `get_release`, `get_release_by_tag`, `edit_release`, `delete_release`, `list_release_assets`, `edit_release_asset`, `delete_release_asset`, `list_tags` |
| `SearchTools` | `search_issues`, `search_pull_requests` |
| `OrgTools` | `get_org`, `list_my_orgs`, `list_user_orgs`, `list_org_members`, `check_org_membership`, `list_org_teams` |
| `NotificationTools` | `list_notifications`, `mark_notification_read`, `mark_all_notifications_read` |
| `WebhookTools` | `list_webhooks`, `create_webhook`, `delete_webhook` |
| `TopicsTools` (optional) | `list_repo_topics`, `add_repo_topic`, `remove_repo_topic`, `set_repo_topics` |
| `ActionsTools` (optional) | `dispatch_workflow`, `list_workflow_runs` |
| `TimeTrackingTools` (Gitea-only) | `list_issue_tracked_times`, `add_issue_time` |

`TopicsTools` / `ActionsTools` are opt-out per forge (a Codeberg instance may lack those
endpoints); `TimeTrackingTools` is Gitea-only and never registered by the GitHub host. See each
host README for the exact selection.

## Hardening seams

**1 — Param validation (`SinapsiForgeValidation`).** Every parameter that reaches a forge URL is
validated at the TOP of the tool, before any HTTP call:

- `ValidateSegment(owner|repo|username|org)` — required, non-empty, ≤ 100 chars, no control
  chars/newlines, no leading `-`, **no path separator** (a single URL segment must not traverse).
- `ValidateRef(branch|tag|ref)` — hierarchical (`refs/heads/x`) allowed, but no control
  chars/newlines and no leading `-`; ≤ 255 chars.
- `ValidatePath` — separators allowed, but a `..` traversal component, control chars, and a
  leading `-` are rejected; ≤ 1024 chars.
- `ValidateQuery` / `ValidateText` — required/non-empty + length-capped; single-line fields also
  reject embedded control chars.
- `ValidateLimit` — positive, ≤ 1000. `ValidatePositiveId` — issue/PR/comment/release ids > 0.

A failure short-circuits with `{ ok:false, status:null, error }` **before any HTTP request**.

**2 — Config fail-closed + timeout clamp (`ForgeClientOptions.ReadHttpTimeoutMs`).** Each host
binds a canonical HTTP-timeout env var with a default (100 000 ms) and a hard ceiling
(600 000 ms). A non-numeric, `<= 0`, or out-of-range value **throws on startup**, naming the
offending variable, rather than binding an unbounded or instantly-timing-out `HttpClient`.

**3 — Uniform error sanitization (`SinapsiForgeErrors.Sanitize`).** Every surfaced upstream/error
string — the forge response body baked into a `ForgeApiException`, and any operational exception
message — is scrubbed of PEM **private-key** blocks and `password|secret|token|api-key|bearer|
authorization` assignments (redacted to `[redacted]`) and length-capped. The numeric HTTP
**status** is taken verbatim as the RAW verdict *before* scrubbing, so a redaction can never
change what status a caller is told — only what secret text they never see.

## Configuration

This library reads **no** environment itself except the timeout binding invoked by the host.
Everything else (base URL, token, tool selection, port) is host configuration — see
`src/servers/Forge.Mcp/README.md` and `src/servers/Github.Mcp/README.md`.

| Helper | Purpose |
|--------|---------|
| `ForgeClientOptions.ReadHttpTimeoutMs(envVar)` | Read + validate a host's HTTP-timeout env var (`1..600000` ms; default `100000`). Throws on an invalid value. |

## Security notes

- **Fail-closed config.** The host's forge base URL + token are required (host throws if
  missing). The HTTP timeout is validated to a bounded range; an invalid value fails startup.
- **No secrets in source.** The forge PAT is injected by the host at deploy and held server-side
  on the `HttpClient`; it is never read into a tool response.
- **No secret leakage in errors.** Every surfaced upstream string passes through
  `SinapsiForgeErrors.Sanitize` before it leaves the process — in the adapters' `EnsureOkAsync`
  (where the response body becomes an exception message) *and* in `ForgeToolGuard` /
  `merge_pull_request` (where it is surfaced to the caller). The RAW status verdict is computed
  first, so a redaction can never flip it.
- **Input validation before side effects.** Every tool validates its URL-bound parameters before
  any HTTP call; a hostile value can neither traverse a URL path nor be smuggled into a request.
- **Bounded HTTP.** Every forge call runs under a host-configured `HttpClient.Timeout`; a hung
  upstream surfaces as a structured error rather than pinning a request thread.

## Error contract

Every tool returns a JSON object. On a validation failure it returns
`{ "ok": false, "status": null, "error": "<reason>" }` with **no HTTP call made**. On an upstream
failure it returns `{ "ok": false, "status": <http-status|null>, "error": "<scrubbed>" }`.
`merge_pull_request` additionally returns `{ merged:false, rejected:true, status, reason }` on a
rejected merge (reason scrubbed). All upstream text is scrubbed of key/credential material and
length-capped.

## Consuming it

Inside this repo the servers reference this project directly (`ProjectReference`), so the
solution builds with no package feed. The project is also `IsPackable` (`PackageId` =
`Sinapsi.Forge`), so it can be published to a NuGet feed and consumed externally via
`PackageReference`.

## Testing

```sh
dotnet test test/Forge.Tests
```

The suite covers, against a fake HTTP transport (no live forge): byte-safe `commit_files`,
create-vs-update verb selection, binary blob round-trip, the merge confirm/reject/retry paths,
topic management — plus the **hardening paths**: the `SinapsiForgeValidation` rejection matrix,
the `SinapsiForgeErrors` scrub contract, the fail-closed timeout matrix, tool-guard
**short-circuit** tests (a transport that throws if reached, proving validation fires before
HTTP), the **load-bearing** leg (a transport emits a secret in an error body → the tool returns
`[redacted]`, never the raw secret), and a timeout path.

Target framework: `net8.0`. The only runtime dependency is `ModelContextProtocol.Core`.
