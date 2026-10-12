# ADR 0050: V3.26 Analyst and reporting surfaces

Status: Accepted for V3.26

## Context

Forge already records custody and outside-processor location in inventory, material outcomes and remnants in reconciliation/genealogy, current tool validity, production actuals by shift, and private planning scenarios with recovery options. The remaining brief questions need to reuse those authoritative records while making temporal and access boundaries clear.

## Decision

Add a `tool-use-history` report that evaluates each recorded `Used` event through `ToolValidity.At` at the event instant. Analyst recognizes a named tool in calibration-at-use questions and filters the report to it. The report describes history available at that instant and explicitly limits what can be inferred: use events do not retain machine/activity, so operation-specific eligibility cannot be reconstructed.

Resolve “last shift” against configured shift start, duration, operating days and the site's local clock, then request production output for that shift/date. Route material-outcome questions with an order number to the existing material reconciliation report. Custody and outside processors continue to use inventory's stock-position report; remnants and material outcomes continue to use reconciliation and genealogy.

Add a deterministic Analyst intent for recovery options. It lists and previews only the caller's saved private scenarios using the scenario service and current authorized schedule/readiness engines. The answer is read-only, names its source and scope, and never commits an option. Existing commands remain the only way to make a consequential change.

## Consequences

No schema or dependency change is required. Historical validity is bounded by tool metadata and event history available at the use instant; it cannot establish the activity or machine eligibility omitted from the use event. “Last shift” uses the latest completed configured shift. Scenario previews are bounded to the first 20 private scenarios and recompute against current source data; the answer labels that scope. Source reports preserve their existing authorization and data filters. ADR 0047 remains the record of the roadmap-derived 15 recovery cards; the Forge folder did not contain the brief's canonical card list.
