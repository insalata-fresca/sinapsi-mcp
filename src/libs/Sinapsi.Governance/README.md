# Sinapsi.Governance

The **continuous-trust governance layer** (home-server `docs/64 §3`, `docs/66`, Mission **D1**)
— the framework that keeps the delivery evaluator honest *over time*. Pure, deterministic,
**shadow-only**: it computes trust as **data the evaluator/pipeline reads**; it flips no
enforcement and changes no trust boundary.

It is deliberately a **different mechanism** from the evaluator it governs (docs/64 §2, DDIA
correlated-fault): governance defines its own `ChangeClass` (one-for-one with the C1 evaluator's
`RiskTier` and the docs/65 rubric tiers) rather than compiling against the evaluator.

## The five parts (the D1 in-scope)

1. **Graduated, revocable trust** — `TrustLedger` / `TrustLedgerEntry` / `TrustLedgerConfig`.
   Per-change-class `Score`; auto-proceed authority **ratchets up only on proven shadow
   reliability** (score ≥ earned-threshold AND N consecutive `Reliable` outcomes), **decays
   toward baseline on any `Miss`**, clamped to a **starvation `Floor`** (never punished to
   zero), instantly **`Revoke`**-able (kill switch, bypasses the floor). `TrustPlane` (and
   `Unknown`) carry a **hard ceiling below the earned bar**, so no streak of reliability can
   ever make the trust plane self-clear to auto-allow. `AuthorityFor()` / `MayAutoProceed()`
   are the data the pipeline reads.

2. **Independent audit line + named owner** — `Accountability/` + `Audit/`.
   The Three Lines of Defense for delivery verdicts (First = the C1 evaluator; Second = this
   layer; Third = independent audit + the **named accountable owner: the Operator, Stefano**).
   `IIndependentAuditor` must be a *different owner and different mechanism*; `AuditIndependence`
   enforces that at wire-up (a self-attesting auditor cannot be installed);
   `IndependentAuditLine` runs it and counts dissents.

3. **Scheduled retrospective inspection** — `Inspection/`.
   `RetrospectiveInspectionSampler` draws a **seeded, deterministic** periodic sample of
   auto-proceed decisions for human review ("inspected trust"), plus a **daily North-Star**
   sample to catch drift/sycophancy — time-triggered, not only incident-triggered.

4. **Escalation-rate SLO** — `Slo/`.
   `EscalationSlo` measures the escalation rate over a window and alerts **two-sided**: above
   ~10% ("Overwhelming HITL", an attack surface) and suspiciously low (rubber-stamping), with a
   `MinSample` guard so a few decisions can't false-alarm.

5. **Red-team hook + AIA stub** — `RedTeam/` + `Aia/`.
   `GateRedTeam` runs a seed corpus of untrusted-diff / injection probes against any gate
   delegate and reports breaches of the trust-plane-never-auto-allows invariant.
   `AiaStubLibrary` carries a per-tier Algorithmic-Impact-Assessment stub (the trust-plane
   stub pins `AutomationPermitted = false`, agreeing with the ledger cap).

## Event discipline

Governance signals are **facts, never triggers** — `Events/GovernanceChannels` reuses the C2/C3
`Sinapsi.Nats.EventPlane` rule and publishes under `homelab.governance.*` (captured by the shared
audit stream). Emission is behind `IGovernanceEventSink` so the core stays pure; a NATS-backed
sink is a host concern (deferred). Nothing auto-acts because trust decayed.

## Status

Shadow / not wired to enforcement. Dependencies: B1 rubric (`docs/65`), C2/C3 event plane
(merged). C1 evaluator (`Sinapsi.DeliveryEvaluator`) and B2 grading are, at the time of writing,
**unmerged branches** — this library does not compile against them; the `ChangeClass ⇔ RiskTier`
mapping is the merge seam. A personal research-lab library; offered as-is.
