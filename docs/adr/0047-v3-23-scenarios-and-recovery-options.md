# ADR 0047: Private planning scenarios and recovery options

Status: Accepted for V3.23

## Context

Forge has deterministic scheduling, material readiness and executable-capacity engines. The earlier Analyst what-if could only vary one outage or absence and discarded the result. V3 needs named private cases, current-versus-proposed projections, Skills MRP, and recovery actions that preserve source-domain authority.

## Decision

Store named scenarios in `planning.scenario`, owned by one Forge person. Reads, runs and deletion are owner-scoped; sharing is not enabled by this increment. Save and delete use the command pipeline, reason, audit and event paths. Scenario inputs are serialized, versioned domain values, not copies of production records.

Run each scenario against a repeatable current snapshot, filtered to orders the caller may see. Apply one isolated change (machine outage, person absence, priority, eligible operation machine, overtime, expected-supply delay, operator or trainer assignment) to a copy. Compare the baseline and proposed finite-capacity schedule, MRP material readiness and Skills MRP capacity. Runs write no source state, schedule, event or audit entry.

Return 15 recovery-option cards with feasibility, consequences and the permission needed. The action opens the owning domain screen; any consequential change must be committed there through its normal command and authorization path. A scenario does not execute a recovery on the user's behalf.

## Consequences

The scenario migration is additive. Owner access is enforced in the store and service. Inputs are revalidated when run against current visibility and business rules. Outage/absence windows must overlap the 62-day planning window; the comparison horizon is 14 days. Expected-supply delays affect only the scenario's material-readiness projection, never the supplier's purchase line.

The supplied Forge directory contains the V3 roadmap but not the master brief's enumerated recovery-option list. The current 15 cards cover schedule resequencing, priority, alternate machine, operator reallocation, added operator, trainer, overtime, second shift, supplier expedite, approved alternate, point-of-use transfer, shortage requisition, maintenance window, partial/split build review, and delivery-date coordination. Reconcile this catalog if the source brief is supplied.

## Completion audit: evaluated feasibility and evidence

Cards now expose affected visible orders, current/proposed dates, evidence, blockers, missing inputs and current authority. Alternate-machine candidates run the shared finite-capacity scheduler with actual eligibility, safety, qualifications, tooling, holds and maintenance gates. Evaluation is bounded and any narrowed candidate search is stated. An option requiring unspecified source inputs returns `RequiresInputs` or `NotAuthorized`; the UI does not label an unevaluated action feasible. Actual stoppages carry recorded Execution provenance, and hypothetical scenario windows carry an explicit flag and scenario reference.

Shared capacity inputs now include recorded setup/changeover/stoppages and planned-use tool validity boundaries. `RecoveryGovernanceTests` proves a real machine failure changes capacity, goal risk and scoped notifications, preserves saved forecasts, evaluates candidates and requires a human source command to restore availability. Preview writes no production, audit or outbox state. Final verification is recorded in the release handoff.
