# ADR 0047: Private planning scenarios and recovery options

Status: Accepted for V3.23

## Current checkpoint — 2026-10-13

The recovered original BG catalog supplies the authoritative fifteen option names. The resumed full `verify.cmd` gate passed on 2026-10-13 against the frozen 80-file source manifest on HEAD `7092a11`; the subsequent source commit is recorded in AI_HANDOFF.md. Release build: zero warnings/errors. Planning 303/303, Kernel 33/33, Architecture 32/32, restored PostgreSQL Integration 163/163 in 59.8970 minutes including cleanup, and Node 34/34: 565 passing tests. Evidence: `.forge-logs/verify-v3-reconciled.log` and `.forge-logs/verify-v3-reconciled.status`. Current counts and provenance are recorded separately from historical results in the completion checklist and source reconciliation. This checkpoint supersedes the earlier unavailable-catalog/proxy statement retained below as history.

The catalog is: overtime; additional shift; alternate qualified personnel; alternate machine; alternate work center; approved alternate material; existing WIP; remnant transformation; split production; resequencing; expedited procurement; expedited material movement; outside processing; maintenance timing change; partial shipment. Each has source selections, consequences, affected orders, source command references and current permission results. Cards without necessary inputs remain `RequiresInputs`; denied authority remains `NotAuthorized`; recorded constraints produce `Infeasible`.

Each selected supported option starts from an independent copy of the same authorized scenario snapshot. Calendar/staffing, material allocation, the full visible finite-capacity queue, documentation/training, point of use, tools, holds, qualification, safety and source-specific preparations remain binding. Issue, transfer, processor dispatch and packed-shipment release trials reuse Inventory.Contracts preparation checks without writing business state. Private runs do not combine selections into an implicitly approved recovery and do not invoke source commands.

The bounded implementation explicitly refuses to invent missing source capabilities. Alternate work-center trials cannot establish a source-authorized replacement routing merely from configured capacity; an authorized engineering change and routing application are required. Split-production trials cannot resize a released order or assert a controlled split plan from proposed quantities alone. Outside-processing feasibility describes the real dispatch preparation, not an invented usable return or completion benefit. Partial-shipment feasibility describes release of an already packed source shipment; remaining demand and dates remain unchanged. These are explicit evaluated limitations, not claims that all fifteen options are executable production workflows.

RecoveryCatalog, RecoveryTrialEvaluator and RecoverySourceFacts keep selected trials independent and retain the canonical full material snapshot, true supply/allocation keys and the private scenario's original supplier-delay/priority changes. MaterialSourceVisibility projects inaccessible source explanations and links without altering allocation, netting or recorded fingerprints. Shared version 7 WipContinuationFacts recognizes only the exact existing resolved CompleteAtOldRevision decision. IInventoryRecoveryReadiness in Inventory.Contracts/RecoveryReadinessContracts.cs is implemented by InventoryRecoveryReadiness; issue, transfer, outside-process dispatch and packed-shipment release reuse their actual source-command preparations. RecoveryInventoryReadinessTests requires unchanged source/audit/event/idempotency state.

## Context

Forge has deterministic scheduling, material readiness and executable-capacity engines. The earlier Analyst what-if could only vary one outage or absence and discarded the result. V3 needs named private cases, current-versus-proposed projections, Skills MRP, and recovery actions that preserve source-domain authority.

## Decision

Store named scenarios in `planning.scenario`, owned by one Forge person. Reads, runs and deletion are owner-scoped; sharing is not enabled by this increment. Save and delete use the command pipeline, reason, audit and event paths. Scenario inputs are serialized, versioned domain values, not copies of production records.

Run each scenario against a repeatable current snapshot, filtered to orders the caller may see. Apply one isolated change (machine outage, person absence, priority, eligible operation machine, overtime, expected-supply delay, operator or trainer assignment) to a copy. Compare the baseline and proposed finite-capacity schedule, MRP material readiness and Skills MRP capacity. Runs write no source state, schedule, event or audit entry.

Return 15 recovery-option cards with feasibility, consequences and the permission needed. The action opens the owning domain screen; any consequential change must be committed there through its normal command and authorization path. A scenario does not execute a recovery on the user's behalf.

## Consequences

The scenario migration is additive. Owner access is enforced in the store and service. Inputs are revalidated when run against current visibility and business rules. Outage/absence windows must overlap the 62-day planning window; the comparison horizon is 14 days. Expected-supply delays affect only the scenario's material-readiness projection, never the supplier's purchase line.

Historical catalog before original-source recovery: the supplied Forge directory contained the V3 roadmap but not the master brief's enumerated recovery-option list. The then-current 15 cards covered schedule resequencing, priority, alternate machine, operator reallocation, added operator, trainer, overtime, second shift, supplier expedite, approved alternate, point-of-use transfer, shortage requisition, maintenance window, partial/split build review, and delivery-date coordination. That proxy has been superseded by the recovered BG catalog in the current checkpoint.

## Completion audit: evaluated feasibility and evidence

Cards now expose affected visible orders, current/proposed dates, evidence, blockers, missing inputs and current authority. Alternate-machine candidates run the shared finite-capacity scheduler with actual eligibility, safety, qualifications, tooling, holds and maintenance gates. Evaluation is bounded and any narrowed candidate search is stated. An option requiring unspecified source inputs returns `RequiresInputs` or `NotAuthorized`; the UI does not label an unevaluated action feasible. Actual stoppages carry recorded Execution provenance, and hypothetical scenario windows carry an explicit flag and scenario reference.

Shared capacity inputs include recorded setup/changeover/stoppages and planned-use tool validity boundaries. `RecoveryGovernanceTests` covers a real machine failure changing capacity, goal risk and scoped notifications, preserved saved forecasts, evaluated candidates and human source commands restoring availability. Preview writes no production, audit or outbox state. Historical verification is recorded in the release handoff; the resumed original-catalog trials and expanded source-gate tests passed the renewed gate recorded above.
