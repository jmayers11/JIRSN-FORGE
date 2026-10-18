# ADR 0055: owner rulings on idempotent commands and supplier rejection quantities

Accepted by the owner, 2026-10-18. Implemented in V5 (migration `inventory/0021_split_supplier_rejections`).

## Context

The V5 full-system simulation tags every expected outcome with its source. Six rules it checks were not written in
any ADR or the README: three lived only in a code comment, and three were readings of ADR 0035. The owner ruled on
all six. The owner also asked for a return to vendor whose quantity can be adjusted when the rest of the rejected
material turns out to be fine.

## Decisions

1. **Idempotent commands.** Every command carries an `Idempotency-Key` (README). A repeat of the same request with
   the same key returns the stored result of the first and applies nothing again. A key reused for a different
   request is refused with 409 and changes nothing. This applies to every command, including holds. This rule was
   previously stated only on `IIdempotencyStore`.
2. **Concurrent repeats.** Simultaneous requests with one key take effect once. Each caller gets either the result
   or an in-progress conflict to retry, never a second effect.
3. **One disposition per quantity.** A supplier rejection is decided per quantity, not once for the whole.
   - A disposition may name a quantity up to the rejected quantity. Without one, it decides the whole rejection.
   - Deciding part of a rejection keeps the rejection, reduced to the decided quantity, and creates a new pending
     rejection for the rest. The new rejection names the one it was `split_from`, with the same inspection, part,
     lot, location, supplier and defect.
   - Each rejection is still decided once: a decided rejection refuses a second disposition (`already_dispositioned`).
   - Corrections are new, visible records, never edits of what was decided.
4. **One rejection per inspection.** An inspection with a rejected quantity records exactly one supplier rejection.
   Splitting happens at disposition, not at inspection.
5. **Adjustable return to vendor.**
   - A return to vendor carries the quantity its disposition decided.
   - While it is open (not shipped), quality disposition authority can reduce it, with a reason
     (`POST /api/inventory/rtvs/{id}/reduce`). It can never be increased or kept the same
     (`rtv_quantity_not_reduced`), and cannot change once shipped (`rtv_not_open`).
   - The reduced part does not return to stock. It stays on quality hold as a new pending rejection split from the
     original. It needs its own disposition, normally use as is, which needs a second person with quality approval
     (ADR 0035 §4). A reduced return is never a way around the hold.
6. **Open rejections.** A rejection is open while it is pending, or decided use as is and not yet approved. With
   splits, the material of an inspection is fully decided only when every rejection split from it is closed.

## Consequences

- The rejected total of an inspection always equals the sum of its rejections. Quantities only go down. Every unit
  rejected is in exactly one rejection, and nothing leaves quality hold without its own disposition.
- The database enforces these as well. Triggers refuse raising a rejection's quantity, changing what it rejected or
  where it came from, raising a return's quantity, and changing a return's quantity once it is no longer open.
- `DispositionRejection` gains an optional `quantity`. It is omitted from the canonical request when absent, so
  earlier requests and their idempotency fingerprints are unchanged. `ReduceReturnToVendor` is new.
- The receiving screen offers a quantity on Disposition (defaulting to the whole rejection), Reduce on open returns
  (quality disposition only), and shows "split from" on remainder rejections.
- The simulation's checks for rules 1–6 now cite this ADR. A new scenario returns part of a rejection, reduces the
  return, and decides each remainder separately, accounting for every unit.
