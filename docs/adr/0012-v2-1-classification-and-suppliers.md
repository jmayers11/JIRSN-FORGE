# ADR 0012 — V2.1: item classification, manufacturer parts, supplier master

## Context
The V2 requirements extend Forge for complex manufacturing, MRO and project work. They are additive:
the V2 architecture impact review (approved by the owner) maps each requirement onto the existing
contexts and orders the work into twelve increments (V2.1–V2.12). V2.1 lays the item and supplier
foundations the later increments need: controlled documents (V2.2) attach to items, purchase orders
and suppliers; substitution, kitting and MRO all depend on knowing what kind of item something is and
who may supply it.

## Decisions
1. **No new context.** Item class and manufacturer parts live in Engineering (they describe the item);
   suppliers and approved sources live in Procurement (they describe who we buy from). Procurement reads
   Engineering only through `Forge.Engineering.Contracts`.
2. **One class per item, and it must agree with make or buy.** Classes: Make, BuildToPrint, Cots,
   RawMaterial, StandardHardware, Consumable, Mro, Tooling, Service, OtherControlled. Make items are
   Make; BuildToPrint, Cots, RawMaterial, StandardHardware, Consumable and Service are always bought;
   Mro, Tooling and OtherControlled may be either. The database enforces the same pairing.
3. **Existing items are not guessed.** The migration marks made items Make and leaves every bought item
   Unclassified. Unclassified items keep working exactly as before; the item-classification report and
   the Engineering screen show them so a person classifies each one. An item cannot be made unclassified
   again, and every classification needs a reason.
4. **Manufacturer parts are two-person controlled.** Anyone with `engineering.cots.propose`
   (manufacturing engineers, buyers, maintenance planners) proposes manufacturer + MPN for a bought,
   off-the-shelf class; a different person with `engineering.cots.manage` (engineering approvers)
   approves it. The database also refuses an approval by the proposer. Withdrawal and lifecycle changes
   need a reason. Lifecycle (Active, NotRecommended, LastTimeBuy, EndOfLife, Unknown) is what a person
   recorded from the manufacturer; Forge never infers it.
5. **A COTS item cannot be bought without an approved manufacturer part.** New purchase order lines
   for a COTS item are refused (`no_approved_manufacturer_part`). A COTS item with approved
   manufacturer parts cannot be reclassified until they are withdrawn.
6. **Purchase orders name a supplier record.** The migration creates a supplier for each distinct
   supplier name on existing purchase orders (code = upper-case slug, created by `system:migration`)
   and links every existing order to it; the original supplier text is kept. New orders resolve the
   supplier by code or exact name and refuse unknown ones (`unknown_supplier`).
7. **Holds and approved sources.** Holding, deactivating or reactivating a supplier needs
   `procurement.approve` and a reason. A supplier on hold or inactive takes no new orders and no
   approvals (`supplier_not_active`); receipts against already-approved orders continue. An item with
   approved sources is bought only from them (`not_approved_source`); an item with none may be bought
   from any active supplier, as before. Approving and removing sources need `procurement.approve` and
   a reason, and only bought items have sources.
8. **Everything goes through the command pipeline**: authorization, reason, idempotency, audit with
   old and new values, and events `ItemClassified`, `ManufacturerPartChanged`, `SupplierChanged`,
   `ApprovedSourceChanged`.

## Consequences
- Plants with existing data see their bought items as Unclassified until someone classifies them;
  nothing they could do before is blocked by that.
- The supplier master is the anchor for supplier documents and certificates in V2.2 and for supplier
  performance later.
- Order attachments (owner request at approval) are part of V2.2, controlled documents.
