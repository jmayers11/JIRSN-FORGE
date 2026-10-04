# ADR 0018 — V2.7: unified demand, requisitions and "why was this purchased?"

## Context
V2 §21–24, review scenario 6: purchases are driven by production, but also by maintenance work orders,
MRO replenishment, facility and capital projects, quality, tooling and engineering work. A buyer must be
able to see, for any purchase order line, receipt or issue, what it was bought for — and the answer must
come from recorded data, never a guess.

## Decisions
1. **Demand kinds** (`planning.demand`): Maintenance, MRO replenishment, Facility project, Capital
   project, Quality, Tooling and Engineering join the existing kinds. From Maintenance on, a demand must
   name its source as `kind:key` (`work-order:57`, `project:FAC-2027-014/3`); the database enforces it
   (`demand_source_named`). Contract delivery demand is still created from contracts only.
2. **One requisition queue** (`procurement.requisition`): anyone who plans work that needs a purchase
   (planner, maintenance planner, quality and manufacturing engineers, buyers, program managers —
   `procurement.requisition.raise`) raises a requisition with a part, quantity, need date and a source:
   kind, `kind:key` and words. A second open requisition for the same part and source is refused.
   What was asked for never changes (trigger); status moves Open → Ordered → (reopened) or Cancelled.
   Only the requester or a buyer cancels, with a reason.
3. **From MRP**: a Buy recommendation becomes a requisition whose source is found by walking its pegging
   back through parent planned orders to the root requirements (`PurchaseTraceService.SourceOf`, pure):
   a demand gives its kind and source key; order material gives `order:N`; a substitution gives
   `substitution:…`; anything else is safety stock. Roots of different sources give `Mixed`, keyed by the
   planned order and its date, and the label lists every root.
4. **Purchase order lines keep their source** (`requisition_id`, `source_kind`, `source_key`,
   `source_label`): a line added from a requisition copies the source and marks the requisition Ordered
   (same part required); cancelling the line or its draft order reopens the requisition. The source of a
   line never changes (trigger). Lines without a requisition have no source and say so.
5. **"Why was this purchased?"** (`PurchaseTraceService`) answers for a PO line, a receipt or any ledger
   entry of lot-tracked stock: the source, the requisition and demand, what was received and where it went
   (issued to an order or work order, returned, still in stock). Stock that is not lot-tracked is traced
   to its receipt only — Forge says so rather than guessing. Program-restricted sources are hidden from
   people outside the program.

## Consequences
- Maintenance, projects, quality and tooling buy through the same purchasing controls (approval,
  technical packages, receiving inspection) as production.
- Existing purchase lines have no source; nothing is back-filled.
