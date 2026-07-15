# ApprovalBridge.Broker — the Operator Approval Bridge broker (E1.3)

The coordination core of the Operator Approval Bridge (home-server `docs/66-operator-approval-bridge.md`).
It holds **coordination authority** — which request is approved — but **never a target secret**: the seal
is structural (docs/66 §4, I2). The broker only ever dispatches `action_id` + schema-validated params to
an executor; the secret lives target-side under the target's own identity. Even a fully-compromised broker
cannot exfiltrate a secret it never holds.

**Posture: SHADOW / DORMANT.** Dispatch goes through the merged C2 `IActCommandDispatcher` seam wired to
`NullActCommandDispatcher` (deny-by-default) — the executor (E1.4) does not exist, so the broker runs but
**acts on nothing**. Live approve-channel authz (E1.5) and the go-live cutover are out of scope; standing
this up live is a later, operator-gated trust-boundary flip (docs/66 §10).

## What it does (in_scope, E1.3)

1. **REQUEST intake** (the EVENT — a fact). Validate `action_id` against the E1.1 git-backed allowlist and
   params against its `param_schema`; refuse anything unregistered or malformed **before any operator sees
   it** (deny-by-default). Mint nonce + expiry, write KV `pending`, emit `...requested`.
2. **PENDING STATE** in JetStream KV (`JetStreamKvApprovalStore`, bucket `APPROVAL_REQUESTS`).
3. **ONE-SHOT** — nonce + short expiry + **atomic CAS `pending→consumed` BEFORE dispatch**. Exactly one CAS
   wins, so exactly one execution; replays find `consumed`/`expired` (I3/T3).
4. **APPROVE / REJECT COMMAND** — single-receiver, rejectable; structurally enforces
   `approver_identity != requester_identity` (I7/T1); accepted only for a `pending` request with the
   server-held nonce.
5. **DISPATCH** via the C2 `NullActCommandDispatcher` (deny-by-default) — carries no secret.
6. **EMIT** `requested` / `approved` / `rejected` / `executed` / `expired` FACTS as CloudEvents on
   `homelab.security.approval.<action_id>.<verdict>`, `correlation_id`-joined; unclassifiable → the C2
   `DeadLetterRouter` (deny-by-default, never silent-drop).
7. **PENDING QUEUE READ** (`GET /pending`, added for E1.7). A READ-ONLY projection joining every
   currently-`pending` store entry with its registry `ActionSpec` — the `title` + TYPED params +
   provenance (requester identity, action_id, expiry) the Sentinel Console renders as a pending-approval
   card. Performs no state transition and enforces nothing; the security checks live exclusively in
   `ApproveAsync` / `RejectAsync` (item 4).

## The three security invariants (proven by tests)

| Invariant | Where | Test |
|---|---|---|
| One-shot (atomic CAS `pending→consumed` before dispatch; replays lose) | `BridgeBroker.ApproveAsync` + `IApprovalStore.TryConsumeAsync` | `OneShotCasTests` |
| `approver != requester` (structural self-approval block) | `BridgeBroker.ApproveAsync` | `ApproverNotRequesterTests` |
| Deny-by-default dispatch (nothing executes) | `NullActCommandDispatcher` seam | `DenyByDefaultDispatchTests` |

## Reused C2 contracts (not reinvented)

`Sinapsi.Nats.EventPlane`: `EventPlaneChannels`, `ActCommand` / `IActCommandDispatcher` /
`NullActCommandDispatcher` (with an additive `ActCommandKind.ApprovalBridgeExecute` for this act-path),
`DeadLetterRouter`; plus `Sinapsi.Nats.NatsEventPublisher` for the CloudEvents emission.

## Out of scope

Executor (E1.4), agent MCP request tool (E1.6), live approve-channel authz (E1.5), go-live cutover. No
real action can execute from this service. (E1.7, the Sentinel Console UI, consumes this broker's `GET
/pending` + `POST /approve` / `POST /reject` — see `Sinapsi.SentinelConsole`'s README — and added the
`GET /pending` read endpoint here as the minimal plumbing that consumer needed.)
