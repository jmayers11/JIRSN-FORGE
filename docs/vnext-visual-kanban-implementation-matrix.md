# VNext Visual Plant Operations Kanban — Implementation Matrix

Audit date: 2026-10-22. The supplied VNext brief is a product specification; it does not override existing
domain ownership, permissions or safety enforcement.

## Current capability map

| Area | Current Forge source | Status and boundary |
|---|---|---|
| Plant overview | Factory Horizon (`horizonPage`) reads readiness, approved production schedule, downtime, materials, orders and constraints | Implemented overview and drill-downs; exception causality/deduplication is partial, not a unified root-cause graph |
| Workforce Kanban | `workforce-kanban.js`, `/api/workforce/workers`, qualifications, `/api/readiness/reallocation-plan-preview` | Current-state board and read-only cascade preview implemented. No approved weekly person roster, external time clock, persisted scenario, or atomic roster commit |
| Operation readiness | `GET /api/readiness/operations`, `GET /api/readiness/machines/{machine}?activity=...`, `GET /api/readiness/active-work`; `OrderReadinessEngine` and `OperationReadinessEngine` | Implemented checks and evidence. Operations are eligible to run, not proof of machine telemetry or a running state |
| Downtime and PM | `/api/maintenance/downtime`, `/api/maintenance/work-orders`, `/api/maintenance/plans`, `/api/maintenance/assets` | Recorded state and existing authorized workflows implemented. No telemetry stream or configurable warning horizon was found |
| Machine Kanban | `#/machine-kanban` uses `/api/readiness/operations`, `/api/readiness/active-work`, downtime, asset, work-order, resource and optionally workforce/qualification reads | First read-only signal-light board implemented. Shows evidence and routes to owner modules; not telemetry, machine run-state, or editable status |
| Quality/material readiness | Order readiness, Inventory and Quality services/pages | Existing domain workflows. Machine board can only reflect a blocker surfaced by the authoritative operation-readiness result; no new hold logic belongs in this UI |
| Nexus/time-clock | Repository search found no Nexus or time-clock adapter contract | Absent. Do not infer a no-show, clock-in, payroll event, or integration from Forge presence records |
| Cross-module Kanbans | Existing Orders, Maintenance, Material Plan, Quality, Engineering and Skills pages | Domain views exist; coordinated Kanban boards and cross-module causal exception graph are not implemented |
| Offline, concurrent commit, audit | Existing module command APIs and audit records | Workforce roster concurrency/commit is absent. This UI slice performs reads only and cannot commit a status or schedule |

## Incremental implementation plan

1. Add a read-only Machine Readiness Kanban over current Forge resources, order-operation readiness, recorded downtime,
   assets and maintenance orders. Include reason details, source freshness/errors, accessible status labels, filtering,
   and links to existing authorized maintenance/work-center/order workflows.
2. Derive RED only from authoritative blockers; derive GREEN/READY only when the complete readiness input set is
   available and an eligible operation passes. Use GRAY/UNKNOWN when data is missing, stale-by-failed-read, or no
   current operation is evaluated. Do not invent a PM warning interval or imply telemetry. Display overdue non-lockout
   PM as attention; honor lockout/downtime/readiness as a hard block. Do not expose a status override.
3. Keep Plant Overview as the landing page and add a direct, permission-gated entry to the machine board. Preserve
   existing Workforce Kanban and all underlying command ownership.
4. Defer warning-threshold configuration, telemetry/event freshness, Nexus attendance, unified causal exceptions,
   server-persisted workforce scenarios and atomic schedule commit until their authoritative contracts exist.
5. Add UI contract tests for signal precedence, missing-source UNKNOWN, multiple blockers, accessible detail/actions and
   permission behavior. Record database-backed and live-browser checks separately from automated UI coverage.

## Implementation status (2026-10-22)

Phase B has a first UI slice: `machine-kanban.js` groups machines into Blocked/Down, Attention, Ready and Unknown / Not
verified lanes; filters by work center, signal and machine/order text; shows readiness evidence, recorded downtime,
maintenance, assigned staff when permitted, and links to existing owner pages. Plant Overview and Workforce Kanban
link to it. Signals derive from existing Forge results only. Missing operation readiness stays Unknown, and the board
has no write route or manual override. UI coverage is in `tests/ui/machine-kanban.test.mjs`.

No migration or API contract was added. The UI calls the existing endpoints. `Ready` means an eligible operation's
Forge checks pass at the shown read time; it does not mean `RUNNING`. Warning thresholds and telemetry do not exist in
the repository and were not invented. The complete VNext acceptance remains unmet.

## Claude review checklist

- Verify RED precedence for recorded downtime, restricted assets, failed assigned work and a scheduled-now operation;
  verify simultaneous blockers remain visible.
- Verify a missing matching readiness result, failed read, inaccessible permission, or no current operation cannot
  be shown as Ready. Check that the board never labels Forge operation readiness as telemetry or running state.
- Check that role permissions gate resources, readiness, maintenance, workforce names and qualifications separately;
  absence reasons and restricted personnel details must not leak.
- Confirm overdue PM is only Attention unless an authoritative Forge lockout/blocker exists. No UI action clears any
  maintenance, safety, material or quality state.
- Review separate API read timing, stale/error copy, narrow tablet layout, keyboard details, color-plus-text cues and
  reduced-motion behavior (the board has no pulse animation).
- Confirm links land in the owner module and no status drag, schedule commit, timekeeping, maintenance completion or
  quality disposition command was introduced.

## Safety and truthfulness constraints

- A green card means **Ready**, based on the operation engine and data read at the displayed time. It does not mean
  **Running**; no machine telemetry source was identified.
- A failed operation is shown as **Blocked** with the engine's actual primary constraint and explanation. A downtime
  record or restricted production asset is shown as **Down/Out of service**. The UI cannot clear either state.
- All contributing blockers are retained and shown; resolving one does not hide another.
- A source read failure or missing readiness evidence never defaults to green. No readiness row for an idle machine is
  **Unknown / no evaluated operation**, not ready.
- Existing workflow links lead to their owner module. No new drag-to-transition, maintenance completion, quality
  disposition, staffing commit, or timekeeping command is introduced here.

This audit and plan do not claim that VNext acceptance criteria are met.
