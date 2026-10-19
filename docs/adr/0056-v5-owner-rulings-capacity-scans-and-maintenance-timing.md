# ADR 0056: owner rulings on projected capacity, scanned codes and maintenance completion time

Accepted by the owner, 2026-10-18. Records current behavior; no code or schema change.

## Context

Phase B of the V5 full-system simulation (`docs/v5/V5-simulation-phaseB-results.md`) passed every check. Two of the
rules it checked were not written in any ADR or the README: the simulation inferred them. It also recorded that
completing a preventive-maintenance job in the same instant it was started fails. The owner ruled on all three.

## Decisions

1. **Projected capacity follows home work center and shift.** Projected capacity counts a person in the work center
   they belong to (their home work center) and on the shifts they are assigned to. A current assignment to other work
   (`POST /api/workforce/workers/{id}/assignment`) changes what can run now, through readiness and the safety rules,
   but does not move projected hours between work centers. Absence (call-out, PTO, training, unavailability) and a
   suspended qualification do reduce projected capacity, for the hours they cover.
2. **Scanned codes.** `POST /api/identification/resolve` answers:
   - a well-formed Forge code that names nothing the caller may see: **404** `identification_not_found`;
   - anything that is not a Forge code, such as a supplier's or retail barcode: **400** `invalid_identification`;
   - a code from another site's namespace: **400** `foreign_namespace`.

   The answer never repeats the scanned value and never matches the code to something else. A label resolves only once
   a person has observed it printed and attached (ADR 0053/0054).
3. **Maintenance work takes time.** Starting and completing a maintenance work order in the same instant is not a
   supported case: someone writing equipment up and finishing the work in the same moment does not happen in practice.
   Forge currently answers 500 if it does: the planned downtime window would end at its start, which the database
   refuses. This is accepted as is. No change is made, and it is not counted as a defect.

## Consequences

- The simulation's checks for rules 1 and 2 cite this ADR. No check is left needing a ruling.
- A permanent change of a worker's home work center still has no command; a transfer is recorded as an assignment and
  projected capacity keeps the original work center. This remains a finding, not a ruling.
- The simulation lets each maintenance job take its planned duration before completing it, as a technician would.
