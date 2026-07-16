# DeliveryEvaluator.Host — C1 independent delivery risk evaluator (SHADOW bus consumer)

The runnable **bus-consumer host** wrapping the Mission C1 library
(`src/libs/Sinapsi.DeliveryEvaluator`, PR #112). It makes the evaluator deployable as a
**shadow / observe-only** consumer on CT121-mcp-gateway, whose verdict-fact output is the
real-shadow harvest that B2-cert needs to certify the evaluator for enforcement.

Design authority: home-server `docs/64-agentic-delivery-evaluator.md` Track C1 +
`docs/65` risk rubric + `docs/66` continuous-trust governance. Home-server deploy scaffold:
`services/delivery-evaluator/` (README + Quadlet + config, merged in PR #1500).

## What it does

1. **Subscribe** — one durable JetStream consumer on `HOMELAB_AUDIT`, filtered (FilterSubjects,
   plural) to the merge/deploy decision subtrees `homelab.git.>` + `homelab.release.>` +
   `homelab.deploy.>` (`EVALUATOR_WATCH_SUBJECTS`).
2. **Parse** — `ChangeEventParser` turns each observed CloudEvent into a `ChangeSet` (trusted
   `Files` effect + untrusted `Metadata`). Tolerant + fail-safe: an event with no extractable
   effect surface becomes an unparseable `ChangeSet`, which the classifier escalates and
   dead-letters (never a silent allow).
3. **Classify** — `DeterministicRiskClassifier.Classify` (pure path/content rules, **no LLM** —
   structurally a different mechanism from any change author).
4. **Publish** — a `DeliveryVerdictEnvelope` VERDICT FACT to
   `homelab.security.authz.delivery-evaluator.<allow|requires-approval|deny>.delivery-risk-evaluator`
   (unclassifiable → `delivery.dlq.>`).

## Observe-only is STRUCTURAL, not a flag

- The host depends only on `IVerdictFactPublisher` — it can publish a *fact* and nothing else.
- It holds **no** `IActCommandDispatcher`, constructs **no** `ActCommand`, and has **no** code path
  onto the `delivery.command.>` act tree. The act seam stays the deny-by-default
  `NullActCommandDispatcher` (the executor is unbuilt).
- `NatsVerdictFactPublisher.EnsureFactNotAct` fail-closes on any subject that is not a verdict-fact /
  dead-letter subject — defence-in-depth on top of the server-side ACL, which publish-scopes the
  identity to `homelab.security.authz.>` only.
- There is **no enforcement knob**: a verdict is a fact, never a trigger (`docs/64 §3`).

## Config

See `config.env.example`. Env-driven NATS (NKey+TLS), stream/durable/watch-subjects, and health
port (default 8014). The scoped identity seed + CA are host-mounted at deploy, never baked.

## Build

Source image built by `.forgejo/workflows/build-delivery-evaluator.yml` →
`ste/delivery-evaluator:sinapsi-0.1.<run>` + `:sinapsi-latest`, which the home-server
`build-delivery-evaluator.yml` re-publishes as `ste/delivery-evaluator:0.1.<run>` + emits
`homelab.release.delivery-evaluator.published`.
