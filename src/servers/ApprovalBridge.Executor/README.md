# ApprovalBridge.Executor — the Operator Approval Bridge executor SDK (E1.4)

The **target-side** half of the Operator Approval Bridge (home-server `docs/66-operator-approval-bridge.md`
§3.4/§4). Where the broker (E1.3) holds coordination authority but no secret, the executor holds the
**secret** and no coordination authority. It receives an approved, allowlisted act-command, runs the
pre-registered scoped action **under the target's own identity**, reads its own secret via register-secret
**Path D** (generalized: "perform any pre-registered scoped action agent-free"), and returns **only** a
non-secret result conforming to the action's `result_schema`. The secret never enters broker or agent
context — that is the seal (I2), and it is structural, not a promise.

**Posture: SHADOW / DORMANT.** This is a library, not a live worker. The broker keeps
`NullActCommandDispatcher` as its default `IActCommandDispatcher`; the real `ExecutorDispatcher` here is
selected only when the broker is explicitly configured live (`BRIDGE_EXECUTOR_LIVE=true`). Flipping that is
a trust-boundary flip (always-escalate, docs/66 §10) and is out of scope for E1.4.

## The generic SDK (`Sdk/`, `Dispatch/`, `Registry/`)

- **`IActionExecutor`** — the handler contract: `validated params in → non-secret result out`, reading
  secrets target-side via the injected `ISecretSource` (Path D). Action-agnostic (I1).
- **`ExecutorDispatcher : IActCommandDispatcher`** — the real dispatcher the broker can bind to. It: loads
  the action definition from the E1.1 allowlist (`ExecutorActionLoader`), re-validates params against
  `param_schema` (defense-in-depth), resolves the handler by the action's `executor` name, builds a
  Path-D `ISecretSource` **scoped to the target identity**, runs the handler, and validates the result
  against `result_schema` — **plus a declared-keys whitelist** so an undeclared field (a smuggled token)
  fails closed even if the schema leaves `additionalProperties` open. Every failure is deny-by-default.
- **`ExecutorWiring.SelectDispatcher(live, …)`** — the dormancy seam: returns `NullActCommandDispatcher`
  unless `live: true`.

## The Garmin demo executor (`Garmin/`)

`garmin.oauth.exchange` (docs/66 §6) — the smallest slice that exercises the seal end-to-end. Reads the
Garmin client secret target-side (Path D), exchanges the agent-supplied `auth_code` against the token
endpoint, stores the token **server-side**, and returns only `{status, stored, expires_at}` — never the
client secret, never the token. Tested against a **mock** secret source + **mock** token endpoint: no real
Garmin, no live network. The default broker wiring uses `NotProvisioned*` integrations, so even a flipped
live flag executes nothing until the real Garmin integration is provisioned (a separate, out-of-scope step).

## Proofs (tests)

| Property | Test |
|---|---|
| **The seal** — secret read once, target-side, under target identity; never in the result or any ack surface | `SealProofTests`, `BrokerExecutorSealTests` (broker boundary) |
| A mis-authored handler that smuggles a token is refused before it reaches the broker | `SealProofTests.LeakyHandler_IsRefused…` |
| Deny-by-default — wrong kind / no payload / unknown action / bad params / no handler / handler throws | `DispatcherDenyByDefaultTests` |
| **Dormancy** — default is `NullActCommandDispatcher`; `ExecutorDispatcher` only when live | `DormancyTests` |
| Loads the real E1.1 allowlist YAML (incl. `result_schema`) | `ExecutorActionLoaderTests` |
| The Garmin handler returns only the non-secret confirmation | `GarminExecutorTests` |

## Reused C2 contracts (not reinvented)

`Sinapsi.Nats.EventPlane`: `ActCommand` (extended additively with a non-secret `ActPayload` carrying
`action_id` + validated params), `ActCommandAck` (extended additively with a non-secret `ResultJson`),
`IActCommandDispatcher`, `NullActCommandDispatcher`, `EventPlaneChannels`.

## Out of scope

Go-live cutover, real Garmin / live network calls, the Console (E1.7), approve-channel authz (E1.5). No
real action executes.
