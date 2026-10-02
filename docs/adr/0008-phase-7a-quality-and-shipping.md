# ADR 0008 — Phase 7a: quality and shipping

## Context
Spec §45-47: inspections must be independent and traceable to calibrated gauges, nonconforming
material must be held until someone with authority decides, corrective actions must be checked, and
product must leave the plant through a controlled, two-person shipping step. The plan's Phase 7
("programs, quality, maintenance and shipping") is split: 7a is quality and shipping; 7b is
programs/contracts and preventive maintenance.

## Decisions
1. **A new context, `Forge.Quality`.** It owns gauges and calibrations, inspection points
   (part + routing operation + characteristics), inspections, NCRs and corrective actions. It reads
   Engineering and Execution through their contracts; Planning and Inventory read Quality through
   `IQualityLookup`. Shipping lives in Inventory, because shipping is stock leaving the ledger.
2. **Inspection points hang on routing operations.** A plan names the operation it follows and is
   checked against the released routing. Measured (variable) characteristics need a lower and/or upper
   limit; attribute characteristics are accept/reject. Changing a plan never changes history: each
   inspection result copies the limits it was judged against.
3. **Independent, calibrated, immutable inspections.** An operation is inspected only after work has
   started on it, by someone who neither logged labor on it nor reported quantities against it.
   Anything measured names a gauge that is active and within its calibration date. Inspection rows
   are guarded by a trigger: they cannot be updated or deleted.
4. **Failure raises an NCR automatically.** The failed characteristics and the rejected quantity
   become the NCR. An operation counts as passed when its latest inspection passed.
5. **Disposition with a second person where it matters.** Rework, scrap and return-to-supplier are
   decided by a quality engineer (with a reason). Use-as-is accepts nonconforming material, so it
   waits for a quality manager who is not the person who proposed it (also a database check). A
   corrective action is implemented by one person and verified by a different person with
   `quality.approve` (database check too).
6. **Quality is a readiness dimension.** Order readiness (engine v3) fails an unfinished operation
   when an earlier inspection point has not passed or the order has an NCR that is Open or awaiting
   approval. Precedence: Order, Engineering, Predecessor, Quality, Material, then resources.
   The hold is also enforced on the shop floor. Execution owns a small port, `IWorkHoldLookup`
   (in its contracts), and Quality implements it, so Execution never depends on Quality. Starting an
   operation is refused while an undecided NCR holds the order, or while an inspection point before
   that operation has not passed. Reporting good output or completing an operation is refused while an
   NCR holds the order; scrap can still be reported, and anyone working can stop. The kiosk queue
   shows held work with the reason instead of a Start button. A production receipt needs every
   inspection point of the order passed and no undecided NCR.
7. **Shipping is two-person and ledger-based.** A shipment (customer, optional demand, lines of
   part/location/lot/quantity) is created and packed by a material handler; packing checks Available
   stock and that no lot is on an undecided NCR (a lot `WO<n>` belongs to order n). Shipping is done
   by someone with `inventory.shipping.ship` who did not pack it (database check), re-checks stock under
   the part locks and posts `Shipment` ledger entries tied to the shipment. Cancelling needs a reason.
8. **Shipped quantities reduce demand.** Inventory reports shipped quantity per demand; MRP uses only
   what is left to ship, and the demand list shows ordered, shipped and remaining. The demand a
   shipment names is checked by the API (Inventory cannot reference Planning): it must exist, be open,
   be visible to the caller, and every line must be its part.
9. **New roles.** Inspector, Quality Engineer, Quality Manager and Shipper, each holding one duty.

## Consequences
- An undecided NCR or a missed inspection shows on the order, on Quality, as the order's primary
  constraint and on the kiosk; work cannot start past it, and finished goods cannot be received or
  shipped past it.
- Programs/contract quality clauses, first-article inspection and sampling plans are left for later.
