# ADR 0026 — V3.2: material identity and quantity

## Context
The V3 brief (D, E, K) asks Forge to keep a physical instance's identity apart from its quantity. A roll keeps
its number when it moves, shrinks as it is used, and may be divided into narrower rolls or (sometimes) combined
with others. Until V3.2 a lot row was only a label on ledger entries: nothing said what kind of thing it was, and
nothing recorded that one lot came from another.

## Decisions
1. **A lot row is an instance.** `inventory.lot.identity_kind` says what it physically is: lot, batch, roll,
   serial, reel, sheet or piece. Serialized parts are always serials (the migration marks existing ones). The kind
   is set at receipt and never changes; a move keeps number, kind, revision and supplier lot.
2. **Quantity is always the ledger sum.** No instance has a stored quantity. Splits and merges are ledger
   entries (`Split`, `Merge` kinds) at one location and quality status, so every balance stays explained.
3. **A split creates new instances only.** One entry out of the source for the total and one into each new
   instance; the total must be on hand, new numbers must not exist, and what is not divided off stays in the
   source. New instances inherit the source's kind, revision and supplier lot. Serials and parts that are not
   lot-tracked are never split.
4. **Merging is off until a policy allows it.** Combining instances loses their separate traceability, so
   `inventory.merge_policy` is append-only and absent means never. Only `inventory.approve` sets it, with a
   reason; it can require one revision and one supplier lot across the sources and an existing result.
5. **Every split and merge is a link, kept forever.** `inventory.instance_link` (append-only) records relation,
   source, result, quantity, the two ledger entries, reason, time and actor. `InstanceLineage` reads the links:
   direct sources, direct results, and the first instances the line goes back to. V3.3 extends this into the
   genealogy graph across parts (consumption and production).

## Consequences
- MRP, availability and positions are unchanged: instances still sum into the same balances.
- Packing instances into containers and pallets is V3.14 (shipping and packaging), not here.
- Migration count is 54.
