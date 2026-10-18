# V5 full-system simulation: Phase B results

**Run:** 2026-10-18, owner's desktop, full `simulate.cmd` → ****PASS**** (51/51 tests in 35 minutes: 44 scenario runs, 1,680 expectations, 0 failed; 7 of 7 negative controls caught). The log is
`.forge-logs/simulate-pass-20261018-phaseB.log` and the summary page is `.forge-logs/simulation-summary-20261018-phaseB.md`.
**Scope approved by the owner:** test code only. Phase B changes no production code, schema or workflow.

## What Phase B adds

| Part | What it does |
|---|---|
| 15 new scenarios | Amendment scenarios 1–8, 11–14, 16 and 17, plus S10b (split dispositions, ADR 0055). With S09, S10 and S15 from Phase A, all 17 amendment scenarios run. |
| `Engine/Kpis` (KPI collector) | Reads Forge's own planning outputs at each step: executable and working hours per machine and activity over 7 and 30 days, labour hours per qualification, readiness counts and what blocks them, schedule outcome counts (on time, late, unschedulable) and late hours, open constraints, and inventory by status. Written to `kpis.json`. |
| Impact matrix | For each disruptive event, which modules' KPIs changed and by how many readings (`impact.json`, and a cross-module table in the summary). |
| Defect register | Every failed expectation of a real run, with the scenario, seed, rule, expected and actual values, and where the evidence is (`calls.jsonl`: every command, its key, status and answer). |
| Scorecard | Every amendment area with its status, its I scenarios (the area is the scenario's subject) and X scenarios (the area is checked as a consequence of another module's event), and the list of what Forge does not implement. |
| `Engine/Crew`, `SimFactory.BuildPlanningAsync`, `Engine/Documents` | The crew oracle (qualifications, areas, away and trainee status, presence that expires after eight hours); a planning plant on a 24/7 shift with a second planner, a maintenance technician and a part only SLITTER-B can run; document set-up helpers. |

Phase B scenarios run on seeds 42 and 7 (they are slower because they read the planning engines); Phase A scenarios keep seeds 7, 42 and 1009. `.forge-logs/simulate-batch.cmd` (local, not committed) runs only Phase B through `FORGE_SIM_FILTER=Phase=B`.

## Results

Every scenario passed on every seed. Every step of every run also passed the conservation, non-negativity and
audit-chain invariants.

| Scenario | Checks per seed | What is proven |
|---|---|---|
| S01 operator callout | 58 | Sarah calls out: with one qualified person left, both slitters stop on the two-person rule, and capacity and FIRST-AID labour hours drop by exactly her hours. The reallocation preview names what would stop and start without committing; moving Jennifer restores Slitting and leaves the paint booth unstaffed. On return, presence is re-confirmed and everything recovers. |
| S02 PTO and training | 39 | A suspended SLIT-II qualification removes Sarah's SLIT-II labour but does not stop Slitting (Michael operates, Sarah is the responder). Michael's approved PTO later in the day removes its hours from capacity ahead of time, and Slitting stops while he is away. Re-certification by a certifier restores Sarah. |
| S03 staffing transfer | 38 | A transfer by assignment changes what runs now; projected capacity (home work center and shift) does not move. Two findings below. |
| S04 breakdown and overdue PM | 39 | An overdue lock-out PM stops SLITTER-A; a breakdown of SLITTER-B then makes every order unschedulable with a maintenance constraint. The technician cannot sign off their own work; a planner can. Capacity and the schedule recover only on sign-off. One defect (D1). |
| S05 calibration expiry | 34–46 | A gauge used while calibrated becomes invalid once simulated time passes its due date: a use is refused, a failed calibration does not restore it, a passing one does. The tool-use history records, for every use, whether the gauge was calibrated at that moment. |
| S06 supplier delay | 27 | An approved purchase order makes missing wire expected and the order schedulable on time. The supplier's promised date slips past the due date and the order becomes late; the wire then arrives early and the order is ready and on time again. MRP buys exactly what is still needed. |
| S07 inaccurate inventory | 29 | A count that finds less tape posts nothing until a different person approves it; the requester cannot approve their own. Once approved, the order is short and cannot be scheduled, MRP recommends exactly the shortfall, and a replenishment makes it ready again. |
| S08 receiving with FAI | 57 | From a new supplier, nothing is accepted while FAI is pending or required without a pass. After a signed FAI the lot is inspected in part: some accepted, some rejected and returned, the rest left for later. The return ships and the replacement arrives as a new lot (reusing the returned lot number is refused). |
| S11 engineering revision | 29 | A drawing revision released mid-production leaves the running order on the old revision (legitimately superseded, still executable). A deviation that disagrees with what was released blocks the work until order-release authority reissues the documentation; an engineer cannot. |
| S12 expedited order | 24–26 | A stale preview (older than five minutes), a wrong expected priority and a person without order authority are refused. After the change the expedited order finishes earlier and exactly the orders that were ahead of it on SLITTER-B finish later. |
| S13 document error | 31 | A required but unreleased instruction blocks work once documentation is enforced, and its release clears the block. Two approved rules that disagree are never guessed: the work is held until one is withdrawn with a reason. |
| S14 incorrect scan and failed print | 38 | An unknown Forge code is not found and is not echoed; a foreign barcode is refused. A label resolves only once printing and attachment were both observed: issued, approved, jammed or printed-only labels do not. The issuer cannot approve; a jammed attempt must be reconciled before another print; a confirmed label can only be reprinted through the governed path. Labels never move stock. |
| S16 concurrent transactions | 25 | Two to four terminals scrapping the same lot at once: one wins and the lot ends at zero, never negative. Two shipments of the same lot: exactly one leaves. Two engineers deciding one rejection: one decision, with only its effect. Two managers approving one use-as-is: released exactly once. |
| S17 backup and recovery | 34 | A backup taken mid-run and restored into a new database under a new host gives exactly the state at the backup (same state hash), an intact audit chain, the open rejection still open and none of the later work. A command replayed from before the backup has no second effect, and the restored host accepts new work. |
| S09, S10, S10b, S15 | 37, 99, 54, 23–27 | Unchanged from Phase A and ADR 0055, on seeds 7, 42 and 1009. The S10 seed-42 state hash is still `904C1BE0A0B6`. |
| Reproducibility | 4 each | S09, S10, S10b and S15, each run twice with seed 42: identical event logs and state hashes. |

**Expectations by basis:** Spec 861 (119 distinct rules), Invariant 640, Plan 161, Inferred 18 (2 rules).

**Rules for you to rule on** (each passed, but no ADR or README states it):
1. Projected capacity follows home work centers and shifts, not a current assignment (S03).
2. A foreign barcode is refused as invalid or not found and is never echoed; Forge answers 400 `invalid_identification` (S14).

**Cross-module impact.** The summary page has the full matrix. Examples: a callout moves capacity, labour, readiness
and constraint readings; a supplier delay moves only the schedule until material arrives; a documentation mismatch
moves only readiness.

## Full gate

`verify.cmd`, 2026-10-18 13:36: **PASS**, 944 checks with a 0-warning build: kernel 44, planning 303, architecture 32,
PostgreSQL integration 349 and UI 216. The simulation project builds in it and runs no tests there, as intended. The log is
`.forge-logs/verify-pass-20261018-phaseB.log`.

## Defect register

| # | Severity | Where | What | Evidence |
|---|---|---|---|---|
| D1 | Low | Maintenance, `CompleteWorkOrderAsync` | Completing a PM that holds a planned downtime window in the same instant it was started returns **500** instead of a result. The code ends the window at start + 1 tick (100 ns); PostgreSQL stores microseconds, so the end equals the start and `CHECK (ends_at > starts_at)` fails. A real clock rarely produces this, but the guard written for it does not work, and a database error surfaces as a 500. | Phase B batch 2, S04 seeds 7 and 42, work order 3001 (`.forge-logs/simulate-phaseB-batch2.log`). S04 now lets the 90-minute job take 90 minutes; the finding stays in the S04 report. |

No other expectation drawn from the specification disagreed with Forge.

**Production fix for D1 needs your approval** (Phase D). A one-line fix: end the window at start plus one microsecond, or reject completion in the same instant with a 422.

## Findings (not defects)

1. **A worker's home work center cannot be changed after registration.** There is no command for it: `POST /api/workforce/workers/{id}` registers and answers `worker_exists`. A permanent transfer can only be expressed as an assignment, and projected capacity keeps the original department. (S03)
2. **Overtime is not a concept in Forge.** Extra hours can only be expressed through shift assignment and presence. (S03; already in the Not implemented list, owner decision 5.)

## Oracle corrections during Phase B (simulation errors, not Forge defects)

- A supervisor's presence confirmation expires after eight hours (`WorkforceService.DefaultPresence`). The crew oracle now models expiry, and the scenarios re-confirm presence at each shift handover.
- A reallocation preview reports a person who is the only one on some work as `WouldBeUnstaffed`, not as `WouldStop`. The oracle now expects that.
- The maintenance work-order list wraps each order (`order`, `runHours`, `dueByRunHours`, `parts`). S04 now reads `order.kind`.
- S14 expected a foreign barcode to answer 404 or 422; Forge answers 400 `invalid_identification` without echoing the code, which meets the rule. The check now accepts 400.
- S16 originally expected both shipments for the same lot to be created; Forge may also refuse the second at creation or packing, which is equally safe. S16 now checks that exactly one leaves.
- A stale build: one full run reused a test binary older than the copied sources (the copies kept their original timestamps). The sources are now touched after every copy, and the run above was rebuilt from them.

## Phase B gate

The gate in the architecture document (§6) is: *every implemented module has at least one I and one X scenario, and the scorecard lists untested areas.*

- **The scorecard lists untested areas: met.** Three areas have no scenario yet: CAPA, material allocation and alternates (the optimizer is excluded from V5), and customer concessions. Five items Forge does not implement are listed separately and never counted as passing.
- **Every implemented module has an I and an X scenario: not met.** 8 of 26 areas have both. The table below shows what is missing. The 17 amendment scenarios are each about one subject, so most areas get one of the two; closing the gap needs about a dozen more short scenarios.

| Area | Has | Missing |
|---|---|---|
| Demand management, MRP, Execution, Safety, Shipping, Printing, Reporting | X only | an I scenario |
| Engineering changes, Training, Maintenance, Calibration, Identity, Recovery, Network, Concurrency | I only | an X scenario |
| CAPA, Allocation and alternates, Customer concessions | nothing | both |

## Limits, stated plainly

- Still the reference plant at small size, not the 300-order, 70-operator factory, and no 30/60/90-day consequence runs. Both are Phase C.
- Negative controls cover seven breaks; the Phase B scenarios S02–S06, S08 and S11–S17 do not have their own control yet.
- Reproducibility (two runs, same seed, same state hash) is checked for S09, S10, S10b and S15 only.
- Runtime of the full `simulate.cmd`: 35 minutes for 51 tests.
