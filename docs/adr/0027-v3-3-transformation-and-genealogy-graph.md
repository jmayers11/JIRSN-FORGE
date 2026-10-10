# ADR 0027 — V3.3: transformation and the genealogy graph

## Context
The V3 brief (C, E, F, G, J) asks Forge to say what became of material, not only where it went:
- what was consumed into the output, what was scrapped or lost, what came back as a remnant, what byproducts
  were made, and what is still at the work center;
- issued = consumed + returned + scrap + loss + remaining ± tolerance, with anything unexplained flagged and
  never scrapped to make the numbers add up;
- genealogy as a graph that traces a supplier lot through a master roll, a remnant and a second order to what
  was made and shipped, and back. A move is never genealogy.

Before V3.3 the as-built record (V2.11) answered "what is this order made of", one way, and nothing recorded
remnants, byproducts, loss or what was left at the work center.

## Decisions
1. **Transformation records** (`inventory.transformation`, `inventory.transformation_line`, append-only) say
   what became of material issued to a production order. Each line has a role:
   - **Consumed, Scrap, Loss** name the issued part and lot and add up over records;
   - **Remaining** is what is still at the work center; the latest figure replaces earlier ones;
   - **Remnant** is a new instance of the issued part, cut from a named issued lot and put into a company
     location (ledger kind `Remnant`). It keeps the source's supplier lot, revision and identity kind and is
     ordinary stock from then on: it moves, is issued and transformed again;
   - **Byproduct** is a new instance of another part the order made (ledger kind `Byproduct`). The order's own
     part is received from production as before.
   Scrap or loss needs a reason. Recording against material not issued to the order, or accounting for more than
   was issued (beyond tolerance), is refused.
2. **Reconciliation** (`MaterialReconciliation`) is computed, never stored: per issued part and lot, net issued
   (issues less returns, from the ledger) less consumed, scrap, loss, remnant and the latest remaining. Status is
   Not recorded, Balanced, Within tolerance or Unexplained. An unexplained difference stays flagged (order page,
   `material-reconciliation` report) until a later record explains it. Nothing is scrapped automatically.
3. **Yield tolerance** per part (`inventory.yield_tolerance`, append-only, `inventory.approve`, reason
   required), as a percent of what was issued; absent means zero.
4. **The genealogy graph** (`GenealogyGraph`, Planning) is built from records as written. Nodes are instances,
   receipts, production orders, shipments and assets. Edges are thirteen relations: received, split, merged,
   issued, returned, consumed, scrapped, lost, remnant, produced, byproduct, shipped, installed. Transfers are
   never read, so moving stock can't create a link. The as-built record stays the source for issues; the
   graph does not copy it.
   - A trace goes back (where it came from), forward (what it became), or both, each direction on its own, so
     siblings are not pulled in. It stops at 12 steps or 400 records and says so.
   - Orders in programs the reader cannot see are left out, together with what connects only through them, and
     the trace says how many.
5. **API, UI and Analyst:**
   - `POST /api/inventory/transformations`, `GET /api/inventory/orders/{order}/reconciliation`,
     `POST /api/inventory/yield-tolerance/{part}` and `GET /api/genealogy/trace`;
   - a "What became of the material" panel on the order page, and Trace from inventory;
   - the reports `material-reconciliation` and `genealogy-trace`;
   - Analyst questions such as "Trace roll M-1-R" and "Show unexplained material".

## Consequences
- An order's output is linked to every lot consumed on the order. Per-unit allocation inside one order is not
  recorded, as in V2.11.
- The relation names are Forge's reading of the brief's list of thirteen; renaming one is a label change.
- Migration count is 55.
