# ADR 0048: V3.24 indirect and support work

Status: Accepted for V3.24

## Context

The roadmap requires badged support activities to count as attendance without counting as direct labor, and asks reallocation to offer support work when a machine cannot run. Forge already models these concerns in distinct source contexts: Workforce records badge or supervisor-confirmed presence and assignment; Execution records direct labor against a production operation; Resources defines machine-free work-center activities; Planning evaluates alternatives to a reallocation.

## Decision

Keep attendance and direct production labor as separate records. Do not create a support-work production quantity or a second labor ledger. Reallocation offers a configured work-center activity only after testing its safety composition against the proposed snapshot. The option describes that the original machine work stops and any safety consequences; it does not assign the worker. A user who accepts it commits through the Workforce command, which retains its authorization, reason, event and audit rules.

## Consequences

No migration or dependency change is needed. Existing integration coverage proves attendance/direct-labor separation and production quantity reporting. Planning coverage proves support activities are offered when permitted and that a proposed support duty leaves the original machine work visibly stopped. This ADR records the V3.24 acceptance mapping; it does not claim support activity time is tracked as a separate cost category.
