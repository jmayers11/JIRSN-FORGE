# ADR 0021 — V2.9: maintenance work types, readiness and outage preview

(ADR 0020 is reserved for the goal-risk slice started separately.)

## Context
V2 §25–29, review scenarios 4, 5 and 8: maintenance work is more than preventive and corrective; it needs
parts, a procedure and qualified people; a technician should not start work that cannot be finished, and a
planned outage on a running machine changes the production schedule, so someone other than the maintenance
planner must see that before it becomes capacity loss.

## Decisions
1. **Work types**: Preventive (from a plan only), Corrective, Emergency, Predictive, Calibration, Inspection
   and Facility. A work order is on a machine, an asset, or both (they must agree); work on a building or
   utility asset takes no production capacity.
2. **Requirements** (`SetWorkOrderRequirements`, `maintenance.work_order_part`): the asset, a controlled
   procedure revision, the qualifications whoever does the work must hold, and the parts with the position
   each is installed at. Changed only before work starts; the next preventive order keeps them.
3. **Readiness is decided by a pure engine** (`MaintenanceReadinessEngine`), dimension by dimension, each
   failing on its own and with no score: the asset can be worked on; every part is issued, on hand, or
   covered by an approved alternate usable for this asset and machine (maintenance-only included, proposed
   never); the procedure revision is released and in effect for the window; someone active and available
   holds every qualification for the whole window. Stock is not reserved by readiness.
4. **Blocked work is not planned as capacity loss**: scheduling a blocked work order is refused with the
   reason. What it is short of can be requested from purchasing in one step: requisitions sourced
   `work-order:N` (V2.7), once per part.
5. **Outages are proposals** (`maintenance.outage`): scheduling work on a running machine, or planning
   maintenance on it, proposes an outage. It becomes downtime only when a different person with
   `maintenance.outage.approve` (maintenance or production planning) approves it, after the scheduler has
   been run without and with it (`OutagePreviewEngine`: operations touched, moved, delayed, unschedulable,
   orders made late). The preview summary is stored as the decision's impact. Proposals can be rejected
   with a reason or withdrawn by the proposer; cancelling or starting the work withdraws its proposal.
   A repair of a machine already down needs no proposal.
6. **Installation from issues**: spares issued to a work order whose requirement names a position on an
   asset in service are recorded as installed there (an event subscriber, `work-order-installation`),
   replacing what was at that position, including when an approved alternate is issued in place of the part.

## Consequences
- Readiness and the outage preview are read models over one snapshot; nothing about them is stored except
  the impact recorded on a decided outage.
- Emergency work still starts at once; the outage rules apply to planned work.
