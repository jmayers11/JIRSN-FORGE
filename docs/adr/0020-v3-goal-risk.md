# ADR 0020 - V3 goal risk

V3 starts by making goal risk explicit without creating a second planning system.

## Decision

Add a pure planning engine, `GoalRiskEngine`, that evaluates scheduled production goals against actual
operation completion evidence.

It keeps these states separate:

- `OnTrack`
- `BehindPlanRecoverable`
- `GoalAtRisk`
- `Late`
- `Unschedulable`
- `Complete`

The key V3 rule is:

`BehindPlanRecoverable` is not `GoalAtRisk`.

An order can miss an intermediate scheduled operation and still remain achievable when the current finite
capacity projection completes with enough recovery slack before the due time. Conversely, an order can be at
risk even before an intermediate miss if the projection is inside the configured recovery margin, late, or
unschedulable.

## Consequences

- Goal risk reuses `ScheduleResult` and shop-floor operation facts.
- No database migration is required for this slice.
- No autonomous recovery action is taken. The engine explains the condition; humans decide.
- Future services can persist engine runs, expose the result through APIs/reports, and connect it to recovery
  scenarios.
