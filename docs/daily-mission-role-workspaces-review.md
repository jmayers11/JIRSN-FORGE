# Daily Mission & Role-Based Workspaces — Phase 0 Review

Reviewed 2026-10-25 against `Forge_Daily_Mission_Role_Based_Workspaces_Implementation.md` and the current Forge source.

## Finding

This is a new product feature, not a visual polish pass over an existing Daily Mission page. There is no Daily Mission or Daily Plan route, mission projection, first-login briefing, per-operational-day dismissal record, or mission decision workflow in the current client or host. The attachment is an implementation specification with an illustrative schema; its embedded “implementation request” is treated as project requirements, separate from the owner’s request to review it.

## Existing capabilities to reuse

- `src/Forge.Host/wwwroot/js/app.js` already filters navigation by effective permissions and exposes Home/Plant Overview, Workforce Kanban, Training & Performance, Planning, Shop Floor, Quality, Materials, Maintenance, and My Work as distinct workspaces. Home and Plant Overview currently use the Horizon page.
- `src/Forge.Host/wwwroot/js/pages/workforce-intelligence.js` and `performance.js` already present source-backed workforce coverage, qualifications, attendance, training, performance context, and supervisor recommendations. The Command Center labels recommendations as requiring review.
- `src/Forge.Kernel/WorkAssignments/WorkAssignments.cs` provides audited, permission-checked assigned work with source revalidation. Its lifecycle is `Assigned → Acknowledged → InProgress → Completed`; it must not be used to claim a production order, shipment, quality release, or inventory transaction is complete. A mission may link to this queue where appropriate, while authoritative completion remains in the existing source workflow.
- Forge already has site time-zone support, worker shift fields, role/permission and program-scoped access checks, audited command pipelines, notification delivery, and evidence-bearing workflows for orders, inventory, quality, maintenance, and workforce. These are integration points, not justification for copying their business state into mission rows.

## New work the specification requires

- A server-side, read-only mission projection that gathers only data authorized for the current identity and scope, records source freshness/version, and links each item to its authoritative source.
- Operational-day semantics based on the site time zone **and** assigned shift/calendar, including overnight shifts. Site-local dates and worker shift fields alone do not provide the required once-per-operational-day identity or cross-device briefing state.
- Persistent Daily Plan and first-login briefing state, including dismissal separate from approval, role/scope changes, stale/partial-data notices, and a multi-role workspace selector/preferences.
- Mission-specific accept/modify/defer/reject and conflict handling, if enabled, with expected-version checks, idempotency, audit reasons, permission checks, and revalidation through each existing command. The example `mission_item` status must never become the source of operational completion.
- Role-specific workspace composition, program-scope-safe aggregation, explainable deterministic prioritization, notification/update policy, accessibility coverage, and the WhiskerWorks acceptance matrix.
- Hawk artwork/preferences are optional visual polish and should follow the operational workflows.

## UI review and implementation guidance

The experience principles are strong: action-first, explainable, dismissible, permission-aware, and safety/quality constrained. The document is an interaction/spec brief rather than a visual mockup; it does not define a finished screen composition or a specific role-priority algorithm. The proposed first-login briefing plus persistent Daily Plan is a good shell, provided each card includes its source record, reason, freshness, blocker, and only the next action the user is authorized to take.

The suggested `mission_item`/`mission_decision` entities need to remain a projection and decision log, not a shadow workflow. In particular, do not duplicate work-assignment status, invent a completion checkbox, persist copied inventory or attendance facts, or expose restricted program names/counts in a summary. Use source references and current source reads; mark a projection stale when it cannot safely refresh. Keep the current Classic Workspace available during rollout.

The scope spans an aggregator, time semantics, persistence, eight role workspaces, controlled commands, accessibility, and at least 20 explicit scenarios. It should be delivered in the document’s phases. Start Phase 1 read-only, prove authorization/freshness and operational-day rules, then add the briefing and persistent UI. Enable consequential actions only after each action maps to an existing transaction and passes hard-gate/concurrency tests. Do not build a parallel scheduler or recommendation calculator.

## Current handoff status

The Sprint 9/10 handoff has no newly identified missing production feature. Its outstanding items are acceptance evidence: isolated replay automation for S03, S09, S10, and S12–S16; the 38 Sprint 10 acceptance cases; and the separately noted interactive S07 Scenario Center evidence. S02 now passes its isolated replay within the 28/28 WhiskerWorks run (`.forge-logs/whiskerworks-replay-20261025c.json`). The 2026-10-25 full verification had 819 passes and 4 failures; the four fixed/environment cases were rerun 7/7 green, and UI checks passed, but that is not one uninterrupted all-green `verify.cmd` result.

## Recommended next implementation increment

Add the read-only Daily Mission projection contract and operational-day resolver first. Reuse Forge's authorization and existing service outputs, return source references plus freshness/partial-data state, and add tests for overnight shifts, repeated login, scope isolation, and missing sources before adding action controls. Keep existing routes as the fallback while the new workspace rolls out.
