# ADR 0025 — V3.1: stock dimensions and locations

## Context
The V3 brief (H, BS) keeps apart several things Forge had merged:
- physical on hand, available for planning, allocated and at the point of use;
- location and custody;
- quality status and availability.

Inventory's ledger has one status column, `Available | Inspection | Hold`. Its "Available" meant "released by
quality", but the screens and MRP also read it as "available for planning". Locations were flat and had no
custody.

## Decisions
1. **The ledger status is the quality status, renamed.** `Available` becomes `Released` (decision 1 of the V3
   plan).
   - Migration `inventory/0008` rewrites the value in `inventory.transaction` and `inventory.adjustment_request`.
     Only for this rename does it lift the append-only trigger.
   - The migration fails itself if any balance (part, location, lot) changes or any row is left unrenamed.
   - No quantity, location, lot, actor or time changes. The API now says `Released`.
2. **Locations carry the other dimensions.**
   - **Hierarchy:** `parent_code` and `level` (area, zone, rack, shelf, bin, position). A child is narrower than
     its parent and on the same site; cycles are refused.
   - **Kinds:** staging, machine side, WIP storage, outside processor and in transit join the existing kinds.
   - **Nettable:** whether released stock there counts for planning. The default follows the kind; outside
     processors and in transit are never nettable.
   - **Point of use:** a work center (production, staging, machine side) or a machine (machine side).
   - **Custody:** the company; a supplier (an outside processor holds company-owned material); or in transit.
     Ownership stays with the company.
3. **Stock positions** (`StockPosition`, `GET /api/inventory/positions`) join each balance to its location.
   - Each position carries quality status, location path, custody, nettable, point of use and whether it is
     available for planning.
   - The stock summary reports on hand, released, inspection, hold, not counted, at point of use, allocated
     and available for planning as separate figures.
4. **Only what is available for planning is planned from.**
   - MRP, material readiness, maintenance readiness, project material and MRO levels net released stock in
     nettable locations.
   - Existing locations stay nettable, except quarantine. Released stock in quarantine stops counting.
5. **Custody blocks use.** Stock at an outside processor or in transit cannot be issued, returned into,
   received into, shipped, counted or scrapped. It moves out and back by transfer, and keeps its lot.
6. **Determinations** (blank ≠ unknown ≠ not applicable ≠ complete) are deferred to V3.11, where receiving
   inspection and FAI first use them. A primitive with no user would be speculative (brief BV 10).

## Consequences
- Allocation of physical stock (reservation) is not here. "Allocated" is still what released orders need and
  have not been issued. Reservation, picking and delivery to the point of use come in V3.4.
- Location kind and site cannot change. A location is retired and replaced instead, so history keeps meaning
  what it said.
- Clients sending `"Available"` as a status are refused. The web UI and the sample script send `"Released"`.
