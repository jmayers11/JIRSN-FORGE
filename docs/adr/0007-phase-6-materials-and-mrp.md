# ADR 0007 — Phase 6: inventory, procurement, demand and MRP

## Context
Spec §11-13, §38, §48-49: inventory must be transactional and explainable, MRP deterministic with a
written answer to "why?", and material availability must feed readiness and scheduling.

## Decisions
1. **Two new contexts.** `Forge.Inventory` owns the ledger, locations, lots and adjustment requests.
   `Forge.Procurement` owns purchase orders. Received quantities live only in Inventory (the receipt
   transaction names the PO line), so Procurement never holds a second copy. Demand belongs to Planning.
2. **The ledger is the truth.** `inventory.transaction` is append-only (a trigger rejects UPDATE and
   DELETE); balances are a view summing it. Each command takes a per-part advisory lock, checks the
   balance and refuses to go below zero. Every entry records who, when, why and what it relates to
   (order, PO line, adjustment request).
3. **Two people, everywhere it matters.** A purchase order is approved by someone other than the
   buyer (database check too). Adjustments and count variances are requested by one person and posted
   only when a different person with `inventory.approve` approves (the ledger keeps both names).
   Stock in Inspection or Hold is released by someone other than the person who put it there.
   A count is posted only if nothing moved since it was taken; otherwise count again.
4. **Status, not just quantity.** Stock is Available, Inspection or Hold. Only Available can be
   issued or counts for MRP and readiness; the others are shown, never hidden.
5. **MRP** (`MrpEngine`, pure): low-level codes from released BOMs (loops reported, not followed);
   per item and date, projected available = previous + scheduled receipts − gross requirements; below
   safety stock a planned order covers the shortfall, sized by lot policy (lot-for-lot, fixed quantity,
   minimum, rounded to a multiple) and released one lead time earlier (calendar days). Made items
   explode through the BOM in force on the release date. Gross requirements: open demand, material
   still to issue on released orders (needed now), and the BOM of planned/firm orders. Scheduled
   receipts: approved open PO lines and unfinished production orders, less what was received. Each
   planned order carries its arithmetic and FIFO pegging. Exceptions: past-due releases, receipts to
   bring in, overdue receipts, past-due requirements, missing revisions, loops. MRP is computed on
   demand and not stored; nothing is ordered automatically.
6. **Material readiness** (`MaterialEngine`, pure): available stock, then scheduled receipts by date,
   are allocated to released orders in the scheduler's order (priority, due date, number), so two
   orders never count the same material. Result per order: Ready, Expected (with the date) or Short.
   Order readiness gains a Material dimension. The scheduler (v3) holds an unstarted order's first
   operation until expected material's date, and leaves short orders (or ones waiting on an overdue
   receipt) unscheduled with a Material constraint. Started orders are not held back.
7. **Single-site dates.** MRP works in local calendar days of the first site's time zone.

## Consequences
Released orders now need stock or supply to be executable; the test plant and the sample plant
receive coil. Not yet: serial numbers, phantom assemblies, alternates, yield, supplier records and
performance, backflushing, kitting/picking, shipping and the Horizon material lens.
