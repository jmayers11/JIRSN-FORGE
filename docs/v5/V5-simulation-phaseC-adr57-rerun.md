# V5 simulation Phase C re-run after ADR 0057

Date: 2026-10-19. The same factory, sizes and seed as the Phase C baseline (300 customer orders, 70 operators, 90 days,
seed 42), run on the code with all of ADR 0057 (§1 site time, §2 inspections in the plan, §4 sub-assembly lateness,
§5 worker moves, §3.2 changeover set-up). The baseline's outputs are kept in
`.forge-logs/simulation-factory-baseline-20261019`. The new outputs are in `.forge-logs/simulation-factory`.

## Result

Both runs passed every check: 33 in the baseline and 60 in the disrupted run, with 0 failures and 0 server errors. The
week in Packing is now a temporary move with an estimated end date.

## What changed in the plan

Figures are baseline / disrupted, before and after ADR 0057.

| Reading | Before | After |
|---|---|---|
| Unschedulable orders, day 1 | 78 | 54 |
| Of those, stopped by an inspection not yet done | 59 | 0 |
| Unschedulable orders, day 30 | 102 / 115 | 40 / 56 |
| Unschedulable orders, day 90 | 47 / 53 | 27 / 27 |
| Orders on time in the schedule, day 30 | 9 / 6 | 28 / 34 |
| Scheduled segments started as booked | 3,320 / 3,168 | 3,482 / 3,419 |
| Orders completed by day 90 | 512 / 511 | 516 / 519 |
| Customer orders shipped on time | 124 / 80 | 123 / 86 |

- **Inspections:** inspections no longer make orders unschedulable. The orders that are still unschedulable are short
  of material with no supply (25 on day 90), or cannot fit their work in the 21-day horizon.
- **Late orders:** the schedule now shows them as late with a projected date instead of hiding them as unschedulable
  (day 30: 63 / 53 late, against 27 / 28 before). The delivery forecast covers far more of the factory.
- **Throughput:** throughput is about the same. Slightly more orders were completed and more segments were started as
  booked.

## Finding: the shop floor still waits for inspections the plan already counts on

Started-as-booked rose, but refused starts rose too: 457 in the baseline run and 547 in the disrupted run, against 8
and 15 before.

- **Where they come from:** almost all of them (451 in the baseline run) are `order_on_hold` because "the inspection
  after operation N has not passed". They come from 22 orders.
- **Why:**
  - The schedule now plans the next operation after a pending inspection, which is ADR 0057 §2.
  - The simulation's inspection plans have no planned inspection time, so that operation is booked right after the
    inspected one.
  - The shop floor rightly refuses to start it until the inspection has passed (§2.5).
  - For a few orders the simulation's daily routine never performed that inspection, so their booked segments were
    refused day after day. Before ADR 0057 those orders were silently unschedulable.
- **Next steps (suggested, not done):**
  - Give the factory's inspection plans a planned time.
  - Have the simulation inspect every completed inspected operation, not only test operations of finished products.
  - Report a booked segment held only by a pending inspection as a "waiting for inspection" readiness state rather
    than a refusal.

## Not covered by this run

- Re-planning after a failed inspection runs as an event subscriber. The simulation host does not relay events, so
  this run did not exercise it; `InspectionPlanningTests` does.
- Reminders before a move ends and the automatic return of the worker run on a timer in the host. They are covered by
  `WorkerMoveTests`, not by the factory run.
