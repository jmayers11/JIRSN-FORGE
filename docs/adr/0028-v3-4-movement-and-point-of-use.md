# ADR 0028 — V3.4: movement and point of use

## Context
The V3 brief (I, BB) and spec §49 keep apart the states material passes through on its way to a machine:
required, requested, allocated, picked, delivered, issued and consumed. Each can be partial. Staging, kitting
and put-away are work confirmed by a scan. Stock that exists but is not at the machine is a material-flow
problem, not a shortage, and planning must say which. Cycle counting follows programs.

Before V3.4, material readiness knew only "on hand somewhere" and "issued", and moving material was a bare
transfer.

## Decisions
1. **Material tasks** (`inventory.material_task`) are the movement work. Each is a stage or kit line for a
   released order, a put-away, or a replenishment of a point of use. A task goes requested → allocated → picked
   → delivered, or is cancelled with a reason.
   - **Allocate** names the source location (and lot). The stock is held from other allocations: released stock
     there, less what other allocated or picked tasks hold, must cover it.
   - **Pick** is confirmed by scanning the source location and, for a lot, the lot. A short pick records the
     quantity actually picked.
   - **Deliver** is confirmed by scanning the destination. It is the only step that moves stock: one transfer for
     the picked quantity, carrying the order number.
   - Staging and kitting go only to a staging or point-of-use location of one of the order's work centers, never
     for more than the order still needs.
   - Each step checks the status it expects, so two people cannot take the same step.
2. **Material flow** (`MaterialFlowLine`, Planning) is computed per component of an order, never stored:
   - required (BOM), requested, allocated, picked, delivered (tasks), issued (ledger) and consumed (V3.3);
   - "at the machine" is released stock at the point of use of the work center of the first operation not yet
     complete; "elsewhere" is other released stock in nettable company locations;
   - the state is **Issued**, **At the machine**, **In the warehouse** (a material-flow constraint: not a
     shortage) or **Shortage**.
   Material allocation and readiness (V1 `MaterialEngine`) are unchanged: material in the warehouse still counts
   for readiness. The flow view says what still has to move.
3. **Cycle-count programs** (`inventory.cycle_count_program`) count a location tree, a part or both every N days.
   A balance in scope is due when it was never counted, or its last count (with or without a variance) is older
   than the program's frequency. When several programs cover a balance, the most frequent applies. Counting uses
   the existing count and two-person variance approval.
4. **API, UI, reports and the Analyst:**
   - `/api/inventory/tasks` (request, allocate, pick, deliver, cancel),
     `/api/inventory/orders/{order}/material-flow`, `/api/inventory/count-programs` and
     `/api/inventory/counts-due`;
   - Inventory gains Tasks and Counts tabs; the order page gains "Material to the machine" with Request staging;
   - the reports `material-flow` and `counts-due`;
   - Analyst questions such as "Show what is in the warehouse" and "Which cycle counts are due?".

## Consequences
- Tasks are mutable rows with an audited history of each step, like shipments; the ledger stays append-only.
- Scans are typed or read by a scanner into the same field; Forge compares, it does not drive hardware.
- Migration count is 56.
