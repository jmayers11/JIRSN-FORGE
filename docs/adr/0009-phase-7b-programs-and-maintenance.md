# ADR 0009 — Phase 7b: programs, contracts and preventive maintenance

## Context
Spec §7 (program → customer → contract → CLIN/SLIN → demand → orders → delivery, Program Horizon),
§72 (program segregation) and §47 (preventive and corrective maintenance, work orders, maintenance
feeding capacity and scheduling). Before this phase a program was only a uuid used for access.

## Decisions
1. **A new context, `Forge.Programs`.** It owns programs, contracts (period of performance, ceiling,
   status), CLINs and SLINs (a SLIN names its parent), delivery schedules, contract requirements and
   milestones. A program's id is the same uuid that segregates orders, demand and audit records, so
   existing segregation applies unchanged. Execution and Planning read Programs through its contracts.
2. **Segregation is the default.** Program data is visible only with access to that program; anyone
   else is told it does not exist. Creating a program does not grant its creator access: a security
   administrator grants it (Settings → Program access), and nobody can grant themselves. Security
   administrators see program codes and names only (`programs.directory`), enough to grant access.
3. **Deliveries become demand by a planner's action.** A program manager schedules deliveries on CLINs
   that name a part, inside the period of performance and within the CLIN quantity. A planner plans a
   delivery as `ContractDelivery` demand carrying the program; the demand records the delivery as its
   source, so it is planned once. When the contract changes or a delivery is cancelled, the demand list
   says so and the planner brings it back in step (or it is cancelled) — the two never silently diverge.
4. **Orders need a real, active program.** Creating an order for an unknown program is refused, and a
   closed program takes no new orders.
5. **Program Horizon is counted, not estimated.** Units required are scheduled deliveries; delivered is
   what shipped against their demand; planned is what has become demand. Order status is the approved
   schedule's projection (on schedule, at risk, projected late, not scheduled), and the primary
   constraint is the binding constraint that delayed the most of the program's operations in it.
6. **Preventive maintenance in `Forge.Maintenance`.** A PM plan (machine, interval in days, duration,
   1–50 checklist tasks) keeps exactly one open preventive work order (database index). Signing it off
   raises the next, one interval after the work was done; skipping one (cancel, with a reason) raises
   the next one interval after the skipped due date.
7. **Work orders are two-person.** A technician starts the work (the machine leaves production: a
   planned downtime window from now) and completes it only after confirming every checklist task. A
   maintenance planner who neither started nor completed it signs it off (database check too), or sends
   it back. Scheduling a work order creates or moves its planned downtime window, so capacity, readiness
   and the scheduler see it.
8. **Breakdowns are repaired through work orders.** Reporting a machine down raises a corrective work
   order tied to the outage. The machine returns to service when that repair is signed off. A plain
   restore is still allowed for a fault that needed no repair (it cancels the untouched work order), but
   not once repair work has started.
9. **Lock-out PM.** A plan can be marked safety-critical. Once its work order is overdue past the grace
   days (end of the last grace day, site time), the maintenance lookup reports a derived open-ended
   `MaintenanceOverdue` downtime window for the machine until the work is completed. Because readiness,
   capacity, the scheduler and the shop floor all read downtime through that one lookup, the lock-out
   applies everywhere with no special cases, and it ends the moment the work is completed.
10. **Holds on the shop floor (7a follow-up).** Execution owns `IWorkHoldLookup`; Quality implements it.
    Starting an operation is refused while an NCR is undecided or an earlier inspection point has not
    passed; reporting good output is refused while an NCR is undecided.
11. **New roles.** Program Manager and Maintenance Planner.

## Follow-ups (built after the first 7b release)
12. **Funding.** Obligations are recorded per contract modification (optionally per CLIN), with a reason.
    Total obligated funding never exceeds the ceiling and never goes below zero (a negative obligation
    is a de-obligation).
13. **WBS.** A program's work breakdown structure uses dotted numbers (1, 1.2, 1.2.3); an element's
    parent must exist and its code extends the parent's. A program manager charges a program order to
    one element; Program Horizon rolls orders up through the tree.
14. **Earned value is schedule-based.** Budget at completion is the value of part CLINs; planned value
    is deliveries due by today and earned value what shipped against them, at CLIN unit prices. It also
    shows deliveries scheduled beyond the funding obligated. Forge records no actual cost, so there is no
    CPI and Program Horizon says so.
15. **Spare parts on work orders.** Spares are issued from stock to an open maintenance work order and
    returned from it (inventory transaction kinds `MaintenanceIssue` and `MaintenanceReturn`, carrying
    the work order). A return never exceeds what is net issued. Maintenance technicians and material
    handlers may issue spares.
16. **Run-hour PM.** A plan can be due every N calendar days, every N machine run hours, or whichever
    comes first. Run hours are the machine's recorded labor intervals merged (trainers' time excluded),
    read through Maintenance's `IMachineUsageLookup` port, which Execution implements. A lock-out plan
    locks the machine out once the hours pass the due hours plus the grace hours.

