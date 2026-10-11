# ADR 0031 — V3.7: goal attainment and reforecast

## Context
The V3 brief (A, AW) and spec §42 ask for goals to be broken down, with required-by dates that cascade. Remaining
work and actuals should give a projected completion. Behind plan must be kept apart from goal at risk, and
production goals apart from delivery goals. A paused V3 slice (ADR 0020) had already added `GoalRiskEngine`,
which keeps behind plan apart from at risk for orders in one schedule. The approved plan takes that slice over
here.

## Decisions
1. **Goal risk is taken over, with one change.**
   - `GoalRiskEngine` still measures behind plan against the plan: operations the approved schedule expected done
     by now.
   - It now takes an optional reforecast: a fresh finite-capacity schedule of the remaining work from now. When
     one is given, projected completion and delivery status come from it.
   - Without an approved schedule, the reforecast serves as both, so nothing is behind plan.
   - The engine's tests from the paused slice stand unchanged. `GET /api/planning/goal-risk` exposes the
     per-order view.
2. **Goals are records, kept in two kinds.**
   - `planning.goal` is production (make it) or delivery (get it to the customer).
   - A production goal may follow a production order; a delivery goal may follow a demand line; a goal with
     neither rolls up the goals below it.
   - A production goal breaks down only into production goals; a delivery goal may break down into either,
     because a delivery depends on production, not the other way round.
   - Setting and changing goals needs `planning.goals.manage` (planners, program managers). A change or close
     needs a reason; a goal with open goals below it is not closed.
3. **Required-by dates cascade.** A goal has either its own date or a number of days before its parent's. Moving
   a parent moves every goal that follows it.
4. **Attainment is evaluated, never stored, except as a reforecast.** `GoalAttainmentEngine` evaluates each goal:
   - A production goal follows its order: done is good output at the last operation against the order
     quantity; remaining work is the planned minutes of unfinished operations for the quantity not yet good;
     state comes from goal risk.
   - A delivery goal follows shipments against its demand line, and takes the latest projection of the
     production goals below it.
   - A roll-up goal takes the worst state and the latest projection of its children.
   - Then the goal's own date decides. Past it and not complete is late. A reforecast after it is late; one on
     the day is at risk. Being behind plan alone never makes a goal at risk.
5. **A reforecast is history.** `POST /api/planning/goals/reforecast` records each open goal's required-by date,
   projection, state, behind-plan flag and attainment in `planning.goal_forecast`, which is append-only. The goal
   list shows the latest reforecasts beside the live evaluation, so movement over time is visible.

## Consequences
- The Scheduling page gains a Goals panel: breakdown tree, dates, reforecast, done against target, state and
  evidence.
- The `goal-attainment` report; the Analyst answers "Which goals are at risk?", "Which goals are behind plan?"
  and "Which goals are late?". "Schedule attainment" remains its own report.
- No goal is changed automatically; a reforecast only records.
- Migration `planning/0006`; migration count 60.
