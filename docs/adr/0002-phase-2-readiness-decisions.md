# ADR 0002: Phase 2 readiness slice decisions

Date: 2026-09-30. Status: accepted as defaults; the owner may revise any of them with EHS, HR and
legal input (architecture section 33, items 6, 7 and 14).

## Safety-rule semantics (section 33, item 6)

- **All applicable rules must pass.** Rules scoped to the safety area, work center and machine that
  match the activity are evaluated independently; the work is safe only if every one is satisfied.
- **One distinct person per slot instance.** Slots are filled by maximum bipartite matching, so a
  person may hold several qualifications but fills one slot per rule and never counts twice.
- **Trainees fill a slot only where the rule says so** (`traineeMayFill`, default false). A trainee
  is never an independent machine operator.
- **Presence scope is the whole safety area** (spec §31): everyone present and assigned in the area
  counts toward area rules, whatever machine they are on.
- **Lone-work prohibition** raises the headcount requirement to at least two.
- **No applicable rule means not restricted** ("unusual is not wrong"); the result says so explicitly.
- **Approval:** drafts come from `SafetyEngineer`; publishing needs `SafetyApprover` and a different
  person from the author (also enforced by a database check). A new version retires the previous
  one at the moment of approval; history is kept. Two-person approval for weakening changes is not
  implemented yet.

## Presence policy (section 33, item 7)

- Presence is evidence (`ClockedIn` or `SupervisorConfirmed`) with an expiry: 8 hours by default,
  16 hours at most. A schedule never creates presence.
- Location comes from the current assignment's safety area, not from tracking.
- Unavailability (call-out, PTO, training, unavailable) excludes the person and has no reason field.

## Model additions

- **Work-center activities:** activities allowed at a work center without a machine (staging,
  kitting). Needed to validate assignments and to offer one-person alternatives (spec §35 option C).
- **Qualification lifecycle is simplified** to InTraining → Qualified → Suspended/Revoked. The
  supervised-practice and evaluation stages arrive with the trainer workflow (Phase 5).

## Scope limits of this phase

- Readiness evaluates requirement profile, workforce and safety only; other dimensions are listed
  as not evaluated and never shown as passing.
- Evaluation is "now" only. Future projections (PTO forecasting, executable capacity over time) are
  Phase 4.
- Reallocation options: replacement people (ranked: breaks nothing first), switching the operator
  to one-person work, stopping the operation, adjusting the schedule (manual until Phase 4), and
  manual review. Forge never selects or commits an option.
