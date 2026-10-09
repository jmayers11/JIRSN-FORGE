# ADR 0019 — V2.8: assets, MRO stocking and obsolescence

## Context
V2 §21–22, §25 and review scenario 7: Forge knew machines as capacity, but not the physical assets behind
them (or buildings, utilities and test equipment), what is installed in them, how spares are stocked, or
what happens when a part reaches end of life.

## Decisions
1. **Assets live in Maintenance** (`maintenance.asset`): code, name, type (production, building, utility,
   test equipment, tooling, vehicle, other), manufacturer, model, serial, location, work center, criticality,
   commissioned date, expected life and replacement cost. A production asset points at its machine
   (one asset per machine); the machine stays the capacity unit in Resources and nothing is copied.
2. **Lifecycle history is immutable** (`maintenance.asset_event`): every status change, edit, component
   movement and decommissioning step is a row that triggers forbid changing or deleting. The asset's status
   cannot change without its history row in the same transaction (trigger), and assets are never deleted.
   Statuses: Planned → Active ↔ Restricted, Obsolete; Decommissioned is final and needs a request by one
   person and approval by another with `maintenance.approve`, with no open work on its machine.
3. **Capacity**: a Restricted or Decommissioned production asset takes its machine out of capacity from the
   change until it is returned to service. This is a derived downtime window (`AssetOutOfService`), like the
   overdue lock-out, so the scheduler, readiness and reports need no new rule. Obsolete assets still run.
4. **Components** (`maintenance.asset_component`): item, revision (released), position, quantity and the
   work order that installed it. While an asset is Planned, components are recorded as delivered; once in
   service they are installed and removed only by an open work order on the asset's machine. Removal ends
   the row (work order, who, when, why) and never deletes it. Assets without a machine get work orders in V2.9.
5. **MRO levels** (`engineering.item_mro`, history): minimum, reorder point, maximum, critical spare and shelf
   life for bought items, set with a reason by maintenance planners, planners or inventory controllers. MRP
   treats on hand plus MRO purchases on order as the position: at or below the reorder point it plans a
   purchase up to the maximum, due after the lead time; a shortage before then is planned on its own date.
   What a purchase holds beyond its pegs is pegged to `mro:PART`, so its requisition's source is MRO
   replenishment. A critical spare projected below its minimum is an exception.
6. **Obsolescence records** (`engineering.obsolescence`): status, last-time buy, end of life, end of support
   and the evidence, never edited (a newer record supersedes). A replacement is only what a person names; a
   different person with `engineering.alternates.approve` approves or rejects it. Without an approved
   replacement the item stays flagged.
7. **Obsolescence impact** (`ObsolescenceImpactEngine`, pure): installed quantity, assets and work centers,
   spares, open purchase lines, approved alternates, where-used in released BOMs and open demand and
   requisitions — facts and counts with "Obsolescence action required" when something still uses a
   flagged item. No score and no invented replacement.

## Consequences
- PM plans do not list parts until V2.9, so future demand comes from recorded demand and requisitions.
- Approving a replacement does not change BOMs, alternates or assets; those still change through their own records.
