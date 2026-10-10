# ADR 0030 — V3.6: aging, shelf life and selection

## Context
The V3 brief (M, N) asks Forge to track several things separately: age, shelf life, expiration, open-container
life, retest, and storage conditions and excursions. A shelf-life extension needs authority and keeps the original
date. FIFO and FEFO apply under the compatibility, quality, program and shelf-life rules, never over them. Until
V3.6 the only shelf-life fact in Forge was an MRO policy's number of days.

## Decisions
1. **A part's shelf-life rule is quality's.** `inventory.shelf_life_rule` (append-only; the latest applies) holds:
   - days of shelf life, counted from manufacture or receipt;
   - retest interval and open-container life in hours;
   - storage limits in °C;
   - the selection rule (FIFO or FEFO).

   Setting it needs `quality.plan` and a reason. It applies only to lot-tracked or serialized parts.
2. **A lot keeps the dates it was given.** At receipt a lot gets its manufacture date (from the certificate), its
   expiry and its first retest date. The expiry is the earlier of the rule and the paperwork. A production receipt
   counts as made today. A trigger lets each date be set once and never changed. Split instances and remnants
   inherit their source's dates; a merge is refused when the members' expiries differ.
3. **Changes to those dates are records, not edits.**
   - **Extension:** requested with evidence (`quality.plan`, reason required), decided by a different person
     with `quality.approve`, one pending at a time per lot. The lot's original date stays and the report shows
     both.
   - **Retest:** recorded with evidence (`quality.inspect`). A pass sets the next date; a failure stops the lot.
   - **Opening a container:** starts its open life once.
   - **Storage excursion:** recorded against a location after it is over (reason required). It affects lots
     that were in the location during it, when it broke the part's storage limits (or no temperature was
     given). A passing retest after the excursion clears it.
4. **The status follows from those records.** `LotShelfLife.For` computes the status in this order: expired,
   open life over, failed retest, retest overdue, storage excursion, expiring (30 days), OK, or not tracked.
   Only OK, expiring and not tracked are usable.
5. **Hard rules first.**
   - A lot that is not usable is never issued to an order, project or work order, and never packed or shipped
     (`shelf_life_blocked`).
   - It is on hand but not available for planning: MRP, material readiness, maintenance readiness, the stock
     summary and stock positions all net it out (`StockPosition.Usable`). Project material views do not net it
     out yet.
6. **Selection only orders what the rules leave.** `GET /api/inventory/selection` lists every lot.
   - A lot is left out (with the reason) when it is not released, not in the company's custody or nettable, not
     the revision the order needs, or not usable by shelf life.
   - The rest are ranked FEFO (earliest expiry, then oldest receipt) or FIFO (oldest receipt, then earliest
     expiry).
   - Program-reserved stock arrives with holds in V3.10.
7. **Dates are UTC dates,** like the rest of Inventory (alternates use the same). Across the site's evening, a lot
   can read as expired a few hours before local midnight.

## Consequences
- Report `shelf-life`; the Analyst answers "Which lots are expired?", "Which lots are expiring soon?" and "What
  needs a retest?".
- The inventory page gains a Shelf life tab (pending extensions, lots, excursions). The part drawer shows the rule
  and each lot's dates, with open, retest and extend actions. Receiving takes manufacture and expiry dates.
- Migration `inventory/0012`; migration count 59.
