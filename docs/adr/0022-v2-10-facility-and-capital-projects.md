# ADR 0022 — V2.10: facility and capital projects

## Context
V2 §27–29, review scenario 6: plants spend on facility and capital projects — a gas line for a new slitter,
a roof, a drive upgrade. That material is bought, received and used like production material, but it is not
on any bill of materials and must not be mixed with customer programs. Approval, budget and the financial
reference matter; Forge is not the accounting system.

## Decisions
1. **A new Projects context** (`src/Forge.Projects`, schema `projects`) rather than extending Programs, which
   are customer-facing. A project has a code, title, type (Facility or Capital), funding (Operating or
   Capital), budget, dates, owner, a financial reference (capital request, AFE or cost center) and status
   Draft → Approved → Completed, or Cancelled. Programs segregation does not apply; projects are visible to
   anyone with `projects.read`.
2. **Approval and controlled change.** Whoever manages projects (`projects.manage`: facilities manager,
   maintenance planner, manufacturing engineer) edits a draft directly. A different person with
   `projects.approve` (the new Facilities Manager role) approves it — against a budget, and for capital
   funding only with a financial reference (also a database check). From then on the same commands become
   change requests that need a reason (header, add line, change line, cancel line); a different person with
   approval authority approves or rejects each, and only approval applies it. Decided requests are frozen
   (trigger); nothing is deleted; a closed project is frozen.
3. **Material lines carry a source key** `project:CODE/LINE` (V2.7). The material check is a pure engine
   (`ProjectMaterialEngine`): per line, what is issued, what stock on hand covers (shared between lines in
   need-date order, nothing reserved), what is on order on purchase lines bought for the line, what is
   requested, and what is short.
4. **Shortages become project demand and requisitions** in one step: one demand per line (kind
   FacilityProject or CapitalProject, for the whole line; MRP nets what is issued to the line) and a
   requisition for the shortage linked to it. A second open requisition for the same line is refused, so the
   buyer is not asked twice; once ordered, the purchase line is MRP supply.
5. **Receipts and issues post against the line.** A receipt against a purchase line bought for a project line
   records that line on the ledger entry (an allocation record, not a reservation). Issues (`ProjectIssue`)
   are only to active lines of approved projects and never more than the line still needs; returns
   (`ProjectReturn`) never more than was issued to it; issues less returns is what the line used. A line is
   never reduced below, or cancelled with, material still issued to it.
6. **Outages a project needs** (`ProposeProjectOutage`) are proposed for the project and decided like any
   other outage (V2.9): by a different person, after the schedule preview.
7. "Why was this purchased?" answers with the project line, from the purchase line and from any receipt or
   issue of its lot.

## Consequences
- Spend against budget is not computed: purchase lines have no price in Forge. The project shows its purchases
  and the financial reference to book them against.
- Material received for a project can still be issued elsewhere; the check shows it honestly as stock on hand.
