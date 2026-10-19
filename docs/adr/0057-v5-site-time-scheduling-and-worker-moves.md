# ADR 0057: site time, planning through inspections and sub-assemblies, and worker moves

Accepted, 2026-10-19, from the owner's rulings on the Phase C findings (`docs/v5/V5-simulation-phaseC-results.md`).
The owner approved the work in the order below. Each part records its implementation under "Implementation".

## Context

The Phase C full-factory simulation (300 orders, 70 operators, 90 days) passed every check, but showed five problems in
how Forge plans and keeps time:

1. "Today" is worked out from the UTC date in about 50 places, so at a New York site the day ends at 20:00 (19:00 in
   winter).
2. The scheduler leaves every operation after a planned inspection unscheduled until the inspection has passed.
3. An order whose sub-assembly order is past due is left unschedulable instead of late.
4. An order whose sub-assembly order is due today is scheduled as if the parts were already in stores.
5. A worker cannot be moved to another work center so that planning sees it.

The owner ruled on each.

## Decisions

### 1. Each site's own clock decides "today"

1. Every date rule (calibration, document and alternate effective dates, shelf life, due and expiry dates, report
   periods, goals) uses the local date of the site the record belongs to.
2. Records that belong to no site (for example gauges) use the installation's home site.
3. The site's time zone is set by an administrator. Forge already stores it per site; it gets a screen, and the change
   is audited.
4. It is never taken from a user's device or location. A rule must give the same answer whoever asks and wherever they
   are, and the audit trail must not depend on where someone stood.
5. People may see times in their own time zone. The rules do not.
6. The site's current date and time are shown in the application.
7. One shared function answers "what day is it at this site". Computing a date-only rule from a UTC timestamp is added
   to the banned-API list so it cannot return.

### 2. Inspections take time in the plan and block it only when they fail

1. **Not a block:** an inspection that has not happened yet does not block planning. The operation after an inspection
   point is scheduled after the inspected operation finishes plus the inspection's planned time.
2. **Inspection time:**
   - Each inspection plan gets a planned inspection time in minutes; it defaults to 0, so nothing changes until it is
     set.
   - Inspection time is a gap in the order's flow. It does not book the machine or the operator unless the plan names an
     inspection resource.
3. **Still blocks:** a failed inspection, or a nonconformance (NCR) waiting for a decision, still blocks the operations
   after it, as today.
4. **Re-plan and notify:**
   - When an inspection fails or an NCR is raised, Forge re-plans at once.
   - It notifies the planner with the orders whose projected completion moves, by how much, and why (for example "Order
     10079: projected Nov 4, now Nov 7; inspection after operation 40 failed").
   - The approved schedule stays in force until a planner approves a new one.
5. **Shop floor unchanged:** the shop-floor readiness check, which asks "can this start now", does not change.

### 3. Set-up time

1. **Already in place:** every routing operation has set-up minutes, counted once per order (planned minutes = set-up +
   run × quantity). This stays.
2. **Changeover (proposed; owner to confirm):** set-up depends on what ran before on the same machine.
   - Routing operations may name a set-up family, for example a fixture or material gauge.
   - When the machine's previous job was the same family, the scheduler uses the operation's reduced (same-family)
     set-up minutes; otherwise it uses the full set-up.
   - The scheduler records which one it used and why.

### 4. Lateness travels up through sub-assemblies, with its cause

1. **Plan components first:** the scheduler plans component (sub-assembly) orders before the orders that consume them.
2. **Start from planned finishes:** a parent order's first operation starts no earlier than the planned finish of the
   sub-assembly orders it waits for, in the same schedule. It no longer uses their due dates.
3. **Bought parts:** these use the supplier's promised date.
4. **Overdue purchase order:**
   - An overdue purchase order with no new date makes the waiting orders **late, needing a supplier date**. They are not
     left unschedulable.
   - They are planned from the earliest date the part could still arrive (today plus the supplier's lead time) and
     marked as estimated.
5. **Sub-assemblies show their own lateness:** a sub-assembly order is shown late against when its parent needs it, not
   only against its own due date.
6. **Root cause in the explanation:** an order's explanation names the deepest cause in plain words. For example:
   "Late: waiting for SUB-PANEL (order 10051), which is waiting for SHEET-STEEL on PO 5011, promised Nov 2, not
   received." The same chain is shown on the sub-assembly order and in the delivery report.
7. **Shop floor unchanged:** a component order that has not finished is never treated as material in stores. The shop
   floor's refusal to start without the material stays as it is.

### 5. Worker moves: temporary moves and transfers are separate actions

**Temporary move (loan):**
1. Moves a worker to another work center, with a start date and a required estimated end date.
2. While it lasts, projected capacity counts the worker in the new work center (amends ADR 0056 §1).
3. It can be extended or renewed. Each change records a new end date and a reason.
4. **Reminder:** the supervisor is reminded before the end date (proposed: two working days before).
5. **When no one acts:** if nobody extends it by the end date, the worker is treated as back in their home work center
   from the end date. The supervisor is notified: "Person Y has returned to Work Center Z."
6. It can be ended early, with a reason.

**Transfer:**
1. A separate, deliberate action that permanently changes the worker's home work center from a date, with a reason.
2. Separation of duties as for other workforce changes.
3. The history of home work centers is kept, so past capacity and labor reports stay as they were.

**Schema:** both need new workforce tables (moves with start, estimated end, extensions and outcome; dated home work
center history).

## Consequences

- The schedule's delivery dates become usable for inspected products and multi-level parts. The simulation will show
  this directly: the share of unschedulable orders should fall from 75–100% to the few that are truly blocked.
- **Simulation follow-up:** Phase C is re-run after each change and compared with the 2026-10-19 baseline. The factory's
  transfer event becomes a temporary move; the scenario library gains a transfer, an expired move, and a late raw
  material that makes a parent late through a sub-assembly.
- **Order of work:**
  1. Site time (§1).
  2. Inspections in the plan (§2).
  3. Sub-assembly lateness (§4).
  4. Worker moves (§5).
  5. Changeover set-up (§3.2), if the owner confirms it.

  Each change comes with its own tests.
- **Open for the owner:**
  - the reminder lead time for temporary moves (proposed: two working days);
  - whether changeover set-up (§3.2) is wanted now.

## Implementation

### §1 Site time (2026-10-19)

- `Forge.Resources.Contracts.SiteTime` does the calendar arithmetic in a site's IANA zone with NodaTime's database.
  `ISiteClock` answers "what day is it at this site" from a cache. The cache is loaded after migrations and kept current
  by the create-site and edit-site commands. `uow.SiteDateAsync` reads the zone inside a transaction, for static
  queries and reports.
- The home site is the first site created. Records that belong to no site (tools and gauges, documents, alternates,
  purchasing, goals) use its date. Places that used the first site by code now use the home site.
- About 45 date rules now use the site's date: gauge calibration, tool validity, document, alternate and change
  effectivity, shelf life, QMS and acceptance source checks, quotes, purchase promises, goals, reports, asset status,
  maintenance and history. Pure engines take an optional zone; with none (engine unit tests) they stay in UTC.
- `DateOnly.FromDateTime(DateTime)` is on the banned-API list.
- Tests: `SiteTimeTests` (unit: evening dates, daylight-saving day starts, site clock, a gauge due today at 21:30) and
  `SiteTimeTests` (integration: at 21:30 New York a gauge due today is valid and "tomorrow" is still the future for
  calibration).
- Still to do with Codex: an administrator screen for the site's time zone, and the site's current date and time in the
  application (§1.3, §1.6). The edit-site API already exists and is audited.
