# Sinapsi.DeliveryEvaluator.Grading — Mission B2 (evaluate-the-evaluator)

Grades the **C1** independent delivery risk evaluator
(`Sinapsi.DeliveryEvaluator.DeterministicRiskClassifier`, merged #112) against the **B1** golden
corpus (`ste/home-server:datasets/risk-rubric/seed-corpus.yaml`) — the *"earn trust before
enforcing"* step of `docs/64-agentic-delivery-evaluator.md` §3.

It **reuses C1 by construction**: every scenario's `diff_summary` is fed to the evaluator through
C1's own `CorpusScenarioAdapter`, with the answer-key labels (`tier` / `correct_verdict` /
`is_adversarial`) withheld — exactly the contract in the corpus `README.md`. This library adds *no*
scoring intelligence to the evaluator; it measures it.

## What it computes (`CorpusGrader.Grade`)

| Metric | Definition (corpus README "How B2 uses this") |
|---|---|
| **Overall accuracy** | fraction where verdict == `correct_verdict` |
| **Escalation-recall — THE gate** | over the trust-plane MUST-escalate stratum, fraction NOT auto-`allow`ed. A single miss is critical. Bar = **100%** |
| **False-refusal / over-block** | over `allow`-labelled *low-tier* cases, fraction escalated/denied ("too secure") |
| **Adversarial-subset accuracy** | accuracy restricted to `is_adversarial` cases (injection surface) |
| **Variance** | run-twice determinism — a deterministic classifier must be 100% consistent |

Plus the load-bearing **safety invariants** (no false-allow; no deny-leak), a **per-tier**
breakdown, and the **enforcement-readiness verdict** (`ReadinessGate`) with an explicit bar.

The result is a deterministic `ScoreCard` (`ToJson()`) — a committable, CI-verifiable artifact: a
regression in the evaluator changes the scorecard and fails the golden-file test.

## Stream-diff harness (auto-fail promotion)

`StreamDiffHarness.Diff` replays a **shadow** decision stream through the current evaluator to
compute what enforcement **would** do, and **auto-fails promotion on ANY deviation** or any
record it cannot recompute (fail-safe). `DeviationDirection.MorePermissive` isolates the critical
regression (enforce would *allow* what shadow held). Sources implement `IShadowDecisionSource`
(`JsonlShadowDecisionSource` for CI; the live NATS consumer is the flagged follow-on).

## Honest limit

The B1 seed is **~60 scenarios — directional, not promotion-certifying**. Huyen (*AI Engineering*):
~10k examples are needed to detect a 1% failure-rate difference. A green scorecard here is
**necessary, not sufficient**; the shadow→enforce flip is an operator-gated always-escalate-floor
action (`docs/62 §2.2`) regardless of any score. `ReadinessGate` encodes that: the overall verdict
is **NOT-READY-FOR-ENFORCEMENT** on the seed even when every bar condition holds.
