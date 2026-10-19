# V5 simulation Phase C: full-factory results

Date: 2026-10-19. Test code only; no production or schema change.

## Gate

Phase C (architecture §6): *full-factory mode at the owner's sizes, 7/30/90-day consequences, performance and
concurrency findings, readiness scorecard; outputs 1–10 delivered from executed runs only.*

**Met.** One executed pair of runs at the owner's sizes (300 customer orders, 70 operators, 90 simulated days, seed 42),
a baseline and a disrupted run on the same seed and organization. Both passed every check: 33 in the baseline and 60 in
the disrupted run, with 0 failures and 0 server errors. The ten outputs are on one page generated from the runs:
`.forge-logs/simulation-factory/factory/seed-42/factory-report.md`, with `kpi-comparison.csv`, `timings.csv` and the
day-by-day `baseline-days.csv` / `disrupted-days.csv` beside it. Each run's own `results.md`, `events.jsonl`, `kpis.json`
and `calls.jsonl` (every refused command, with its idempotency key) are under `F01-factory-baseline` and
`F01-factory-disrupted`.

Run it: `simulate-factory.cmd` (about 40 minutes for both runs at owner size; `FORGE_FACTORY_SIZE=smoke` runs 40 orders,
30 operators and 14 days in about 10 minutes). `simulate.cmd` and `verify.cmd` exclude it (`Mode=Factory`).

## The factory (output 2)

Everything is created through authenticated commands, in the order a plant would set itself up.

- **Departments:** 8. SLITTING, PAINT and ASSEMBLY from the reference plant, plus FORMING, WELDING, MACHINING, TESTING
  and PACKING. There are 15 machines plus crew work centers.
- **Shifts:** DAY and SWING, Monday to Friday, with a MACHINING night shift. Shift times are local to the site
  (America/New_York, Forge's default zone), and the run crosses the change from daylight saving time.
- **Safety rules:** two-person rules in Slitting (the reference plant's) and Welding (new).
- **People:** 70 operators with their own qualifications, about a third first-aid responders, some cross-trained, each
  with a badge. The roles behind the routine are held by different people, as separation of duties requires: engineer,
  two planners, buyer, purchasing manager, stores, inventory controller, inspector, quality engineer, quality manager,
  shipper, two maintenance planners and a technician.
- **Parts:** 8 bought parts (sheet steel lot-tracked, with an approved equivalent), 6 made intermediates and 12 finished
  products. BOMs go two levels deep and routings cross departments. Run times are calibrated so each department is
  planned at about 70% load.
- **Supply:** 5 suppliers with 5–14 day lead times. Opening stock covers about three weeks; MRP safety stock is
  1.5 weeks for bought parts and half a week for intermediates.
- **Demand:** 300 customer orders from 12 customers, a quarter open at the start, priorities 1–5.
- **Quality:** an inspection plan with a gauge at every finished product's test operation.
- **Maintenance:** a 28-day lock-out PM plan on every machine outside Slitting and Paint.

**The daily routine, all through the real API:**
- Customer orders arrive. The engineer creates each production order and the planner releases it.
- Deliveries are received against approved purchase orders.
- NCRs are decided: use-as-is by the quality engineer, approved by the quality manager, then re-tested.
- MRP's due planned orders become production orders, or requisitions that become approved purchase orders.
- Finished goods are packed and shipped against the customer's demand.
- A planner proposes the schedule and a second planner approves it.
- The day's work then follows that approved schedule:
  - people clock in for their shift;
  - the order's material is issued at its first operation;
  - each scheduled segment is started and reported by the operator Forge booked, on the machine it booked;
  - tested work is inspected;
  - finished orders are received into stores or finished goods.
- On Saturdays, preventive maintenance that is due is done and signed off.

Random decisions (scrap, test failure) are keyed by what they are about, so both runs make the same decision for the
same work.

## What the runs showed

### Forge carried the whole factory

| | Baseline | Disrupted |
|---|---|---|
| Production orders (customer and MRP) | 559 | 566 |
| Purchase orders raised, approved and received | 71 | 72 |
| Scheduled work segments started as booked | 3,320 of 3,357 (99%) | 3,168 of 3,221 (98%) |
| Operations completed / orders completed | 1,357 / 512 | 1,352 / 511 |
| Units shipped to customers | 7,939 | 7,620 |
| Customer orders shipped complete / on time | 264 / 124 | 250 / 80 |
| Open past their need date at day 90 | 32 | 46 |
| Requests to the API | 25,021 | 24,792 |
| Server errors | 0 | 0 |

**Invariants**, checked at every reading (start, days 1, 2, 7, 30, 60, 90), all held in both runs:
- stock balances equal a recomputation of the ledger, and no stock is negative;
- every shop-floor report applied exactly once (Forge's good quantity per operation equals what the floor reported);
- every approved schedule books no machine or person twice, and nothing on a machine the simulation broke, for a person
  it sent home, or for a qualification it suspended;
- the audit chain was intact at the end.

### Consequences of the disruptions (outputs 4–6)

There are 26 events: the library in §3 of the report page. Each one's immediate effect is measured before and after it
(§4), and its 7, 30 and 90-day consequences are the disrupted run minus the baseline (§5–§6).

- **Machine and people events move capacity at once.** The CNC-1 breakdown halved machining capacity and added about
  500 late hours to the proposed schedule. The machinists' PTO cut CNC-I labour by a quarter, and the TEST-I suspension
  cut test labour by 10%. Each recovered on sign-off or return.
- **The damage compounds.** By day 60 the disrupted factory had produced about 5,900 fewer good units and had 62 more
  open orders. By day 90 it had largely caught up on completions (511 vs 512), but late: customer orders shipped on time
  fell from 124 to 80, and days late in Forge's delivery report rose from 2,289 to 3,182.
- **The calibration lapse stopped inspection exactly as specified.** 22 inspections were refused while the gauge was
  out of calibration, and none was recorded with it. Packing waited for the re-test.
- **The network day and the race behaved as specified.** On the network day, 8 shop-floor reports lost their response
  and were retried with the same key, and each counted once. The two terminals completing the same operation at once
  produced exactly one report.

### Performance (output 9)

The tests run on an in-process host against a disposable PostgreSQL database on the owner's PC. Shop-floor commands are
fast: clock-in p95 15 ms, report p95 30 ms, start p95 337 ms (start runs the full readiness check).

| Work | Typical time |
|---|---|
| Daily schedule proposal for 43–156 released orders over 21 days | p50 about 0.6 s, max 1.3 s |
| MRP read | p95 226 ms |
| Readiness of all operations | p95 1.1 s (the slowest read) |
| The quantity-states report | p95 1.1 s |

Nothing degraded over the 90 days.

## Defect register (output 8): candidates for triage

None of these failed a check, because none is written down as a rule. They are what the factory showed; each needs an
owner ruling or a fix decision. Production changes need approval.

1. **"Today" is the UTC date in about 50 places.**
   - What happened: on the gauge's last valid day, an inspection late in the swing shift was refused as out of
     calibration while earlier ones that day were accepted. `QualityService` compares the due date with
     `DateOnly.FromDateTime(now.UtcDateTime)`.
   - Why it matters: at a New York site the UTC date changes at 20:00 (19:00 in winter). The same pattern appears in
     about 50 places (documents, alternates, shelf life, goals, reports, QMS), so swing and night shifts can be refused,
     or see the wrong day, an evening early.
   - Evidence: the disrupted run's results (finding) and `calls.jsonl`.
2. **The schedule cannot see past an inspection that has not happened yet.**
   - What happened: `OrderReadinessEngine.QualityCheck` judges a future operation by today's quality state. Every
     operation after a planned inspection point stays unscheduled until the test has actually passed.
   - Why it matters: every finished product has an inspection plan, so orders show **Unschedulable** with no projected
     completion. Three-quarters or more of released orders were unschedulable every day of the baseline (78 of 81 on
     day 1, 59 of those for this reason). The schedule's delivery forecast is blind for inspected products.
3. **Material waits on a late sub-order make orders unschedulable rather than late.**
   - What happened: an order waiting for an intermediate from a production order that is past its due date is reported
     "overdue, not received" and left unscheduled. It isn't scheduled behind that sub-order's own projected finish.
   - Why it matters: 26 orders at day 90 in both runs were unplaced this way.
4. **The schedule and the shop floor disagreed about material 8 times in the baseline (15 disrupted).**
   - What happened: an approved schedule booked an order's first operation, the issue came up short, and the start was
     refused with `operation_not_ready` ("Waiting for … SUB-FRAME").
   - Why it might happen: the scheduler's material gate looks at each order on its own, so stock may be counted for more
     than one order.
5. **A transfer cannot be planned (ADR 0056, standing).** There is no command to change a home work center. The
   week-long transfer is a shift change plus an assignment, and projected capacity keeps the old work center.

**Not defects** (expected refusals of the disrupted plant): inspections refused while the gauge was out of calibration;
the losing terminal of the race (`order_not_released` once the order completed); one requisition for a planned-order key
that had changed by the time it was raised (`planned_order_not_found`; raised again the next day).

## Readiness scorecard (output 10)

- All 26 areas are covered: 20 in the factory runs and 6 at scenario level only. The six are CAPA, engineering changes,
  document control, identity/labels/scans, printing and infrastructure/recovery, covered by S11, S13, S14, S17, S23 and
  S25.
- Every factory area passed its checks.

**Readiness:** Forge ran a 300-order, 70-person, 90-day factory with every invariant holding and no server error, and
followed its own schedule 98–99% of the time. Before a production pilot:
- the UTC-date handling (candidate 1) should be fixed;
- candidates 2–4 should be ruled on, because they make the schedule's delivery forecast unreliable for a plant like this
  one.

## Limits of this run

- One seed at owner size. Reproducibility per seed was established in Phases A and B.
- The 25% test-failure window raised few extra NCRs: only about three orders reached test each day in that window.
- The skipped PM's lock-out did not move the readings taken that morning.
- Money is not measured (owner decision).
- The factory keeps one host per run; recovery is covered by S17 and S18.

## Simulation-side fixes made while running it

The smoke runs found these in the simulation, not in Forge:
- Orders were created and released by the same planner. Forge refuses that (separation of duties); the engineer now
  creates them.
- Shift times were taken as UTC; the site is America/New_York. The simulated clock still answers in UTC because the
  database accepts only offset 0.
- A run that never released or ran anything could pass. It now has to show orders released, schedules approved, work
  started and operations completed.
- The calibration-lapse check counted the due date itself as lapsed.

Separately, the PC's PostgreSQL crashed (out of memory) on 2026-10-18 because thousands of leftover `forge_test_*`
databases from interrupted runs had built up. The owner replaced the data folder with a fresh one (the old one is kept as
`data-old`). Nothing in Forge was at fault.
