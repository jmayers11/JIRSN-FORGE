# ADR 0045: Data-integrity exceptions

Status: Accepted for V3.21

## Context

Database constraints and domain commands prevent many invalid states, but operational diagnostics still need to detect impossible records caused by legacy imports, privileged intervention, or defects. Findings must state what the records show and avoid implying intent.

## Decision

Expose a bounded, read-only exception projection at `/api/integrity/exceptions`. Recompute findings from authoritative ledgers and histories for negative inventory balances, duplicate asset serials, a physical material serial with positive stock at more than one location, consumption without a prior matching issue, and tool use after its recorded calibration due date. Filter each finding using the permission of its source domain. Return the observed record evidence and source; label every row `DataOrProcessException`.

No detector edits or repairs source records. A later human repair must use the source domain's normal command and audit path. The current My Work workflow is separate; findings are not automatically assigned.

## Consequences

The UI presents evidence in Data Integrity and reports silence when no visible finding exists. Detector query mappings need to change when source schemas change. Tests seed invalid states directly into isolated PostgreSQL databases to verify detector behavior without weakening production constraints.

`InventorySerialIntegrityTests` distinguishes unique physical serials from divisible lots. A valid serial
transfer leaves a zero balance at its previous location and raises no exception; an ordinary lot may retain
positive stock at two locations. A seeded second positive location for the same serial raises
`SerialInMultipleLocations` with both locations as evidence. The three scaled reference factories also
exercise this detector and must remain silent.
