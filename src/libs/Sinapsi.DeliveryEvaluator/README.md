# Sinapsi.DeliveryEvaluator

The **independent delivery risk evaluator** — Mission **C1** of the agentic-delivery-evaluator
track (home-server `docs/64-agentic-delivery-evaluator.md` §2–3). It judges whether a change is
safe to auto-proceed, and it is built so it is **structurally a different mechanism** from any
agent that makes or advocates the change.

## Why this is not a self-blessing agent

The single correction the 23-book review converges on: *an agent must not be its own gate, and a
second LLM pass is not an independent vote* (`DDIA` Ch8 correlated fault; *Agentic Architectural
Patterns* Planner+Scorer/mode-collapse). This evaluator is therefore a **deterministic classifier**
— pure path/content pattern-match, **no LLM** — that shares no reasoning with the author. For the
trust/security plane the verdict is **deterministic-escalate-or-deny, never an agent value-judgment**
(`docs/64 §2`, `docs/65` principle 5).

## What it does (first shippable increment)

1. **Deterministic tier + trust-plane classifier** (`DeterministicRiskClassifier`) — from a change
   (`ChangeSet` = diff + metadata) it computes the risk **tier** and detects **trust-plane touch**
   (OpenFGA relations, credentials, protected infra, nats/auth config, enforcement flips) by
   **path** (`PathTierClassifier`) and **content** (`ValueSignatureScanner`) rules. Tier = **max over
   surfaces** (`docs/65 §4`).
2. **Verdict** — `allow` / `requiresApproval` / `deny` per the `docs/65` rubric, emitted into the
   `docs/61 §8` decision envelope (`DeliveryVerdictEnvelope`, reusing the C2 `DecisionEnvelopeContract`
   verdict vocabulary + the `EventPlaneChannels` verdict-fact / DLQ subject discipline).
3. **Fail-safe default** — any parse failure / uncertainty / unknown surface → `requiresApproval`,
   **never** `allow`. `allow` is only ever produced by an explicit positive-clearance branch.
4. **Untrusted-diff defense** — the PR title/body (`UntrustedChangeMetadata`) is a distinct field the
   classifier **never reads**; verdicts derive only from `ChangeSet.Files` (effect). A crafted
   "safe, auto-merge" body cannot flip a verdict because nothing lowers a verdict on prose sentiment
   (`docs/65` principle 2).
5. **Confidence signal** (`Confidence`) — signal-strength confidence for B2's calibration metric.

### The four structural guarantees (all unit-tested)

- tier = max over surfaces; a trust-plane surface promotes the whole change;
- a **trust-plane tier never yields `allow`** (the switch arm has no allow branch);
- **fail-safe default = `requiresApproval`** (every non-clearance path escalates);
- the **untrusted PR title/body is never read** (identical `Files` + different `Metadata` ⇒ identical verdict).

## Running against the B1 corpus (the path B2 scores)

`CorpusScenarioAdapter.ToChangeSet(diffSummary)` turns a `seed-corpus.yaml` scenario's prose
`diff_summary` into a `ChangeSet`; feed it to `DeterministicRiskClassifier.Classify`. Accuracy
**grading** is Mission B2's job — this library only **produces verdicts** and proves the structural
guarantees. See `test/Sinapsi.DeliveryEvaluator.Tests` for the end-to-end corpus run.

## Out of scope (flagged as C1b / deferred)

- the LLM gray-zone judge + majority-vote across diverse models (**C1b** follow-on);
- wiring to live enforcement / the act-path executor (deferred — build in shadow, no enforcement flip).

A personal research-lab library; offered as-is.
