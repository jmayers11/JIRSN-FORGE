# V3 governance expansion — requirements register

Source: Product Owner supplied the 55-section “JIRSN FORGE V3 — Operational Governance, Identity, Tooling, Shift Continuity & External Processing Expansion” in this chat on 2026-10-11. This register summarizes that supplied brief; it supplements the supplied `JIRSN Forge V3 Plan.md`, which defines V3.1–V3.28.

The current Product Owner request is to continue until V3 is done and document the outcome. The supplied brief contains an architecture-review-first/approval stop instruction. That is recorded as source-document context; the current direct request authorizes continuation. Architecture impact review still precedes new expansion implementation. Existing working architecture must be extended, with no competing sources of truth.

## Required capabilities and boundaries

| Source sections | Requirements |
| --- | --- |
| 1 | Extend Production, Inventory, Transformation, Genealogy, MRP, Scheduling, Readiness, Workforce, Qualifications, Quality, Procurement, Maintenance, Documents, Configuration, Goals, Scenarios, Horizon, What Changed, CauseMap, Notifications, Audit and Analyst. Do not create unrelated parallel systems. |
| 2–6 | Nexus authenticates; Forge authorizes manufacturing. Forge receives validated standard tokens/assertions, never Nexus passwords. Support enterprise federation through the identity authority. Identity, authentication, application access, roles, permissions, assignment, qualification, approval and signature remain distinct. |
| 7–10 | Configurable roles and reusable business-action permissions. Evaluate operational scope (facility, work center, program, contract, responsibility, thresholds, time, state and qualification). Successful login does not qualify an operator. |
| 11–14 | Configurable authentication assurance and step-up through Nexus; explicit signatures bind identity, authority, assurance, time, record/version, action, meaning, result and audit evidence. Shared terminals show the current actor, lock after configurable inactivity, support secure switching and authenticate each consequential operator action. |
| 15–16 | Approved temporary delegation with grantor, recipient, permission subset, scope, start, expiry, reason and audit; expires without base-role changes. Configurable separation of duties. |
| 17–21 | Tools are Resources with identity, type, condition, custody/location, applicability, calibration, service/sharpening, inspection, usage and limits. Readiness checks eligible tools. Evaluate validity at historical use time and retain use relationships. Service and calibration are distinct. |
| 22–27 | Structured pass-on linked to existing business records. Supervisor handoff reuses current authoritative state; humans add context. Work-center checklists are configurable. Creation, review, acknowledgment, action assignment and action completion are distinct. Notifications do not duplicate or resolve source issues. |
| 28–32 | Auditable controlled priority changes and factual impact previews. Priority cannot override execution gates. Generalized holds have kind/scope/source/reason/evidence/affected records/resolution/release authority. Propagation follows configured factual rules; release may require assurance/signature/approvals. |
| 33–35 | Distinguish planned/unplanned downtime, setup, changeover, waiting, starved and blocked. Capture observed reasons without claiming root cause. Feed actual capacity, schedule, goals, projections, constraints and notifications. Compare planned and actual changeovers without invented explanations. |
| 36–40 | Company-owned material may leave for processing while ownership remains unchanged. Record processor custody, quantities, configuration, specification, PO/service order, shipping/return dates, certifications, receipt, inspection, discrepancies and evidence. Reuse ledger and genealogy. Movement alone is not transformation; actual transformation determines same/new identity. Evaluate readiness before external shipment. |
| 41–43 | Shift closeout derives production/material/WIP/downtime facts. Detect reconciliation gaps and impossible states; preserve unknown quantities and investigate. Never silently classify discrepancy as scrap or inconsistency as misconduct. |
| 44–47 | Recovery scenarios evaluate feasible alternatives and their constraints, requirements, consequences, commitments and projections. Human commits. Domain events drive configurable communication rules. Analyst answers listed identity/authority/tooling/handoff/priority/hold/downtime/outside-processing/closeout/recovery questions using accessible evidence. |
| 49–52 | Keep Nexus identity, Core employment/HR/time authority, Forge manufacturing, and Sentinel security assessment ownership separate. All consequential actions evaluate assurance, permission, scope, qualification, context, duties, signature and execution gates. Fail safely; tolerate integration outages only under controlled policy. |
| 53–55 | Architecture review covers existing/reusable/partial/missing capabilities; owners, entities, database, services, API, events, UI, authorization, audit, integrations, failure, performance, migration, testing, dependencies and product decisions. Challenge reuse with an unlisted legitimate workflow. |

## Acceptance scenarios (source section 48)

- A: Expired required gauge blocks execution with exact evidence.
- B: Historical inspection uses calibration valid at its own timestamp.
- C: Shared terminal cannot attribute B's transaction to departed A.
- D: Configured quality release requires Nexus step-up plus Forge authority and complete signoff.
- E: Authentication/role cannot override expired operator qualification.
- F: Temporary scoped authority works only during its approved interval.
- G: Handoff derives downtime, priority, open material request, quality hold and partially consumed roll; incoming acknowledgment preserves open problems.
- H: Priority proposal previews displaced commitments before authorized commit.
- I: Lot hold shows factual downstream operations, allocations, orders, shipments and goals; configured prohibited actions stop.
- J: External coating round-trip proves identity, custody, ownership, quantity, configuration, genealogy, procurement, evidence, inspection and audit.
- K: 1,000 issued / 750 consumed / 200 returned / 30 scrap leaves a 20-unit exception.
- L: Machine failure updates capacity/goals, communication rules and recovery scenarios; human chooses response.

Completion must be supported by tests and traceable evidence. A described capability or proposed design is not implementation.

