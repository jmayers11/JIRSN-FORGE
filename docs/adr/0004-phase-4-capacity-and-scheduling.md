# ADR 0004: Phase 4 capacity, scheduling, constraints and drop-dead dates

Date: 2026-10-01. Status: accepted as defaults for owner review.

## Time

- Sites have an IANA time zone; shifts are local times with a duration (overnight shifts allowed)
  on ISO weekdays. NodaTime's bundled time-zone database makes results identical on every server
  and correct across daylight-saving changes. A closure date skips every shift starting that day.

## Forecast presence (architecture section 33, items 7 and 14)

- Forecasts project people from their shift, minus recorded unavailability, at their **home work
  center**. Projected presence is labelled "Projected" and is never used for current safety
  decisions, which still require recorded presence evidence.
- Qualifications are projected to expire at their recorded expiry; training that may complete in
  the future is not projected.
- Safety rules are evaluated as in force now; future-dated rule versions are not projected.

## Executable capacity

- Working time comes from work-center shifts. Within it, the state can change only at known
  instants (shift edges, downtime edges, absence edges, qualification expiry), so each requirement
  is evaluated once per span between them. Limiting dimension precedence: Equipment, Safety,
  Workforce.
- Machine windows are individually feasible and share people, so they are not additive. Qualified
  labor (Skills MRP supply) is reported as projected hours per qualification per local day.

## Scheduler

- Deterministic, regenerative, finite capacity. Released orders are taken by priority, due date,
  then order number; operations in sequence after their predecessor.
- Each operation takes the eligible machine that finishes earliest (ties by machine code), with a
  crew of distinct qualified projected people. Machines and people are booked once at a time.
- Operations may continue across shifts. A pause for anything other than a normal shift end is
  recorded as an interruption with its cause.
- Every operation records its binding constraint: Now, Predecessor, Calendar, Equipment, Safety,
  Workforce, MachineBusy, OperatorsBusy or RequirementProfile, with detail.
- Delivery status: Late if completion is after the end of the due date (site local), AtRisk within
  8 hours of it, Unschedulable if any operation cannot finish within the horizon.
- In-progress operations are scheduled as if not started until shop-floor progress exists (Phase 5).
- **Two people:** a schedule run is stored as a proposal; a different person approves it. Only one
  proposal and one approved schedule exist at a time; older ones are kept as discarded or superseded.

## Constraints

- Found only from what the scheduler recorded. Maintenance, safety staffing, qualified labor,
  missing requirement profiles and horizon shortfalls are always reported; ordinary queuing
  (machine or people booked) only when it makes an order late or at risk.
- One finding per category, subject and local day. A later run that no longer finds it marks it
  Resolved (by `system:scheduler`); if it reappears it reopens. People can pick it up
  (Investigating), resolve it with a note, and verify it; the verifier must differ from the resolver.
- Alternatives list people projected at work elsewhere who hold a relevant qualification.
- Known limitation: constraint evidence text may mention an order number from a program the reader
  cannot see; affected-order lists and schedules are filtered.

## Drop-dead dates

- Need-by is the end of the due date at the site. Ship-by subtracts shipping days (calendar days,
  default 1). Each operation is placed backward in its work center's working time; material is
  needed by the first operation's latest start; purchased components must be ordered by material
  date minus supplier lead time (calendar days). Capacity is not considered: these are deadlines,
  not a plan.
