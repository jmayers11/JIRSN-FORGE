# ADR 0042: V3.18 shift continuity

Status: Accepted

Date: 2026-10-11

## Context

Shift changes need a reliable pass-on from current work-center state. The V3 plan requires an editable work-center checklist, a handoff that supplies existing operational facts without re-entry, separate creation/review/acknowledgment/action-completion states, and a closeout that reconciles production, material, WIP, downtime, tooling and unresolved issues. Acknowledging a handoff must not imply that its assigned actions are complete.

## Decision

Execution owns checklist configuration, immutable handoff snapshots, review and acknowledgment records, follow-up actions, and closeout snapshots. Planning supplies the source facts from their authoritative modules in a single scoped read. Handoff and closeout records reference these snapshots; they do not mutate production, material, WIP, downtime, tooling, maintenance or quality records.

Checklist items are configured per work center. A handoff is unique for a work-center/shift/period and can be created only after the period ends. Required checklist entries must be resolved or explicitly marked not applicable with a note. Review and incoming-worker acknowledgment are separate records and actors. Follow-up actions have their own assignment and one-way Assigned → Acknowledged → InProgress → Completed transitions, actor/time history, reason, source link and inherited program scope.

Closeout is unique for a work-center/shift/window and records a point-in-time reconciliation of operational facts plus unresolved checklist items, actions and handoff acknowledgment gaps. Shift boundaries are validated in the site's IANA time zone, including daylight-saving transitions. Read snapshots filter program-scoped facts against current principal access; revoking access does not grant continued visibility through a historical handoff. Audit events record the accountable transitions without duplicating sensitive source facts.

## Consequences

The added Execution tables are append-oriented records around existing source modules. Planning remains the composition boundary, so closeout reports reflect current authorized source facts while completed handoffs preserve what was known at their creation. Duplicate handoff and closeout submissions are rejected by database uniqueness constraints. An over-reported production total is surfaced for reconciliation review and is never silently reclassified as scrap.

The API is exposed at `/api/execution/shifts`. PostgreSQL integration coverage verifies required checklist enforcement, timezone/daylight-saving boundaries, duplicate protection, separate review/acknowledgment, action history, WIP snapshotting, outstanding action carry-forward and closeout uniqueness.
