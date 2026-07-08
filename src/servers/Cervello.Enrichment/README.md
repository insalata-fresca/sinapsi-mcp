# Cervello.Enrichment — the open-points loop from Claude web/mobile

This module is the **cervello knowledge-grounded enrichment engine** (missions E2–E5). Its
operator-facing surface is **two MCP tools, and nothing else**: the operator's ONLY UI for the
enrichment loop is Claude web / mobile via the cervello connector (Surface A). There is **no
separate app** — the open-points tools ARE the UX.

This README is the **project-instruction artifact** (mission E5, task 5): drop its "How the operator
uses this" section into the cervello Claude Project's instructions so the connector knows the loop.

---

## The two tools

### `cervello_open_points_list(kind?, recording?)`

Lists the operator's **pending open-points** — questions the engine could not answer with enough
evidence and withheld rather than guess. Each entry is **redacted** (refs + a one-line question +
scored candidates; never a transcript body, audio, or embedding — lint R10):

```json
{ "pointId": "op_…", "kind": "speaker | correction | link | timeline",
  "recording": "rec://<id>", "bundle": "bundle://<id>",
  "question": "<one-line, redacted>",
  "candidates": [ { "value": "guilhem", "confidence": 0.55, "why": "voice 0.55; filename prior" } ] }
```

- `kind` (optional) — filter to `speaker` / `correction` / `link` / `timeline`.
- `recording` (optional) — filter to one recording (`rec://<id>` or the bare id).

### `cervello_open_points_answer(point_id, answer)`

Resolves one open-point. `answer` is one of:

- **select** a candidate by value → applies that candidate;
- **value** — supply a free value the candidates did not offer;
- **dismiss** — omit the fact (the speaker stays "unidentified"); the dismissal is recorded, nothing
  is guessed.

On a resolving answer the engine:

1. writes the confirmed fact to `map/` carrying `source:` + `confidence` + **`basis: human://<answer-id>`**
   (a human-confirmed fact, never a bare guess — lint R9), as ONE back-linked review-PR (never
   auto-merged; a human merges it);
2. marks the point resolved — **idempotent**: a resolved point cannot be double-applied;
3. for a **correction** point → updates the historized correction map / glossary so the same term
   **auto-corrects next time** (the learning signal);
4. for a **speaker** point → triggers voiceprint **enroll/refine** from the confirmed segments —
   **only** if the person is on the §10 enrollment allowlist (a non-allowlisted person is still
   attributed on the human basis, but no biometric is written).

---

## How the operator uses this (paste into the cervello Project instructions)

> Your cervello enrichment queue is reachable through two tools. To clear it:
>
> 1. Call **`cervello_open_points_list`** to see what needs a decision. Each item has a redacted
>    one-line question and a few scored candidates — that is enough to decide; you never need to
>    open the raw recording or transcript.
> 2. For each item, call **`cervello_open_points_answer`**:
>    - if a candidate is right, **select** it;
>    - if none is, supply the correct **value**;
>    - if it genuinely can't be identified, **dismiss** it (the speaker stays "unidentified" — the
>      engine will never guess).
> 3. A speaker answer enrolls that person's voice so future recordings attribute them automatically
>    (allowlisted people only). A correction answer teaches the glossary so the term self-corrects
>    next time.
> 4. Every applied fact opens a review-PR against `ste/cervello` for you to merge — nothing is
>    written to the map without your answer, and nothing is ever auto-merged.

---

## Guarantees (why you can trust the answers)

- **Token-gated.** Both tools require the cervello-scoped bearer; a missing/invalid token is a 401
  (like M5's `/search`) — this private-plane surface is never open. An unconfigured gate fails
  **closed**.
- **Scoped + logged.** The tools operate only within the cervello workspace (project-binding,
  DESIGN §2.3) and every call is appended to the access log.
- **Never guessed.** The engine emits an attribution/correction ONLY with a valid `source:` + a
  parseable `basis` (`auto://…@…` or `human://…`). Everything ungrounded is escalated (an
  open-point) or omitted — never invented. This is enforced by the system-level never-guess
  acceptance test.

---

## Auto-apply is gated until the threshold is validated

The engine ships in **escalate-only** mode: every attribution is an open-point, so nothing
auto-applies until the voiceprint cosine threshold is **re-fit on the real enrollment set** and
**validated on held-out recordings** to the operator's acceptance bar (min TPR / max FPR). The
offline harnesses (`ThresholdRefitHarness`, `HeldOutValidationHarness`) do this; graded auto-apply
is then enabled **by configuration only** (a `DecisionBands` value + `PolicyPhase.GradedAutoApply`),
with no engine code change.

---

## Deferred live adapters (deploy slice)

Built + tested against fakes here; the live adapters swap in at the CT146 deploy (same seam pattern
as E2b/E3/E4 — no logic change):

- `IOpenPointStore` → the CT146 `open_points` Postgres table (in-memory in tests);
- `IOpenPointsAuthGate` → the Bridge connector JWT/OIDC + cervello project-binding (a static-bearer
  gate proves it here);
- `IAccessLog` → the CT-side cervello access log;
- `IEnrollmentSourceProvider` → the recording's diarized cluster centroids (transient, CT146);
- `ICorrectionMapStore` / `IVoiceprintStore` / `IMapPrWriter` → the E3/E4 live `Pg*` + graph-writer
  adapters;
- the two tools' MCP wire exposure → a Bridge.Mcp `BridgeOpenPointsTools` pair calling the cervello
  backend (mirrors `BridgeCareerSearchTools` → indexer `/search`).
