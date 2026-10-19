# V5 full-system simulation: Phase B results

**Run:** 2026-10-18, owner's desktop, full `simulate.cmd` → **PASS** (51/51 tests in 35 minutes: 44 scenario runs, 1,680 expectations, 0 failed; 7 of 7 negative controls caught). The log is
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
| S04 breakdown and overdue PM | 39 | An overdue lock-out PM stops SLITTER-A; a breakdown of SLITTER-B then makes every order unschedulable with a maintenance constraint. The technician cannot sign off their own work; a planner can. Capacity and the schedule recover only on sign-off. Each job runs for its planned duration (ADR 0056 §3). |
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

**Rules that needed a ruling.** Two passing checks were inferred, not written: projected capacity follows home work centers, and how a foreign barcode is answered. The owner ruled on both (ADR 0056 §1–2) and the checks now cite it.

**Cross-module impact.** The summary page has the full matrix. Examples: a callout moves capacity, labour, readiness
and constraint readings; a supplier delay moves only the schedule until material arrives; a documentation mismatch
moves only readiness.

## Full gate

`verify.cmd`, 2026-10-18 13:36: **PASS**, 944 checks with a 0-warning build: kernel 44, planning 303, architecture 32,
PostgreSQL integration 349 and UI 216. The simulation project builds in it and runs no tests there, as intended. The log is
`.forge-logs/verify-pass-20261018-phaseB.log`.

## Defect register

None. The first run recorded D1: completing a PM that holds a planned downtime window in the same instant it was started answers 500, because the window would end at its start (start + 1 tick rounds to the same microsecond and `CHECK (ends_at > starts_at)` refuses it). The owner ruled that maintenance work takes time and that same-instant completion is not a supported case (ADR 0056 §3), so it is not a defect and nothing changes. S04 lets each job run for its planned duration.

No expectation drawn from the specification disagreed with Forge.

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

The first full run met only half of it: 8 of 26 areas had both kinds. The owner asked for the missing scenarios (follow-up below). With S18–S27 the scorecard shows **26 of 26 areas with both an I and an X scenario**, and it still lists what Forge does not implement (the allocation optimizer, overtime, a 90-day horizon, physical printing, costing), never counting those as passing.

## Follow-up, same day: owner rulings (ADR 0056) and ten more scenarios

**Owner rulings.** Recorded in `docs/adr/0056-v5-owner-rulings-capacity-scans-and-maintenance-timing.md`:
1. Projected capacity follows home work center and shift; an assignment changes only what runs now.
2. Scanned codes: an unknown Forge code answers 404 `identification_not_found`; anything that is not a Forge code answers 400 `invalid_identification`; neither repeats the value.
3. Maintenance work takes time. Completing a job in the same instant it was started is not a supported case, so the former D1 is **not a defect** and no change is made.

The S03 and S14 checks now cite ADR 0056 (no check is left needing a ruling), and the defect register is empty.

**Ten scenarios close the gate.** Each is about one subject (its I area) and checks other modules as consequences (X areas):

| Scenario | I | X | What is proven |
|---|---|---|---|
| S18 production execution | Execution | Training, Maintenance, Calibration, Allocation, Concurrency, Network, Recovery, Warehouse | An order made from release to finished stock: a person with no presence evidence cannot start; a trainee needs a trainer; a down machine cannot start; material is issued exactly to the BOM and no more; a lost report retried counts once; a second host on the same database keeps open labor; two terminals reporting the rest at once leave one report; a measured characteristic needs a calibrated gauge; finished goods are received. |
| S19 lone-work rule | Safety | Workforce, Scheduling | A drafted rule does nothing; its author cannot publish; once published, solo painting stops and the booth's projected hours are zero; borrowing Sarah as responder lets painting run but stops slitting; projected hours do not move (ADR 0056 §1). |
| S20 demand change | Demand | MRP | Demand raised, added and cancelled (each change with a reason, only by demand authority): MRP plans exactly the open spools and the cores beyond stock each time. |
| S21 MRP planned purchase | MRP | Procurement, Demand | MRP buys exactly what demand lacks; a requisition from the plan keeps its quantity and demand, and cannot be raised twice; an approved PO stops the plan; added demand is planned exactly. |
| S22 customer shipment | Shipping | Holds, Warehouse, Demand, Network | Pack and ship by different people; a lost ship response retried ships once; no shipment beyond the lot; a held lot never leaves until released; the demand shows what shipped. |
| S23 label print failures | Printing | Identity | A failed print is final but does not block a new attempt; a confirmed label is reprinted only through the governed path; a replacement ends the old label on approval; a voided label never resolves; labels never move stock. |
| S24 reports | Reporting | Maintenance, Warehouse, Execution, Shipping | The inventory, downtime, output and delivery reports equal what was posted; a reader without permission is refused. |
| S25 corrective action | CAPA | MRB, Execution, Concessions, Reporting | Each NCR holds only its own order; scrap and use-as-is under a concession; CAPA needs the right authority and a note; the implementer cannot verify even with authority; the NCR report shows both decisions. |
| S26 approved alternate | Allocation | MRP, Engineering changes, Warehouse | A proposed alternate changes nothing; approved but only suggested, MRP still buys; once engineering makes the line automatic, MRP allocates the equivalent; issues never exceed the need and never use an unapproved part; an expired alternate is no longer offered. |
| S27 customer concession | Concessions | MRB, Holds, Calibration, Execution, Shipping, CAPA | A wide roll fails inspection and raises an NCR that holds only its order; use as is under the customer's concession needs a second person; re-inspection, final inspection, receipt and shipment follow; the NCR closes and a CAPA is verified by someone else. |

**Oracle corrections in this batch (simulation errors, not Forge defects).**
- Labour hours are sums of per-day rows rounded to 0.01 h, so a window that starts a few minutes later can differ by a rounding step. Labour checks in S01 and S02 now allow ±0.05 h and show both values.
- A supervisor's confirmed presence counts as presence at the terminal, as a clock-in does. S18 checks the refusal with Alex, who has neither.
- A gauge is checked only for measured (variable) characteristics; an attribute-only inspection ignores and does not record it. S18 now measures width.
- SLITTER-A has nobody assigned in the planning plant; S19 reads SLITTER-B, as S03 does.
- A planned maintenance window is only proposed until a second maintenance planner approves it (it takes no capacity before that), and the downtime report counts hours up to now. S24 has the window approved and reads the report after it ends.
- `lastDays=1` on a report means today only. S24 reads its reports over yesterday to tomorrow, so a run that crosses midnight still sees its own records.

**Results:** full `simulate.cmd`, 2026-10-18 19:33–20:21: **PASS**, 71/71 tests. That is 64 scenario runs with 2,466 expectations and none failed; all 7 negative controls were caught, and the four reproducibility pairs matched. Every check now cites the specification, an ADR, an invariant or the plan: no check is left inferred. The log is `.forge-logs/simulate-pass-20261018-phaseB-gate.log` and the summary page is `.forge-logs/simulation-summary-20261018-phaseB-gate.md`.

## Limits, stated plainly

- Still the reference plant at small size, not the 300-order, 70-operator factory, and no 30/60/90-day consequence runs. Both are Phase C.
- Negative controls cover seven breaks; most Phase B scenarios do not have their own control yet.
- Reproducibility (two runs, same seed, same state hash) is checked for S09, S10, S10b and S15 only.
- Runtime of the full `simulate.cmd`: 48 minutes for 71 tests.
