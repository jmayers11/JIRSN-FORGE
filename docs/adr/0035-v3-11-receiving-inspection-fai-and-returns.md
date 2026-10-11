# ADR 0035 — V3.11: receiving inspection, FAI and returns

## Context
The V3 brief (O, P, R, AW) asks for:

- receiving inspection with partial acceptance;
- first article inspection (FAI) with the states required, not required, not applicable and pending determination;
- structured results, plus a signed document where policy asks;
- supplier dispositions;
- a return-to-vendor lifecycle, where replacement material is a new instance;
- customer returns.

The acceptance tests are: a receipt can be partially accepted; FAI pending determination is never shown as complete;
a replacement from an RTV is a new instance, and the rejected one keeps its history. The plan moved the
determinations primitive here from V3.1, so it ships with its first users.

Until V3.11 a receipt into inspection could only be released or put on hold as a whole, and an NCR could only say
"return to supplier".

## Decisions
1. **Determinations** (`quality.determination`, append-only) record whether something applies to a subject, with a
   basis, decided by quality planning. The first question is FAI.
   - The subject is `PART:REV@SUPPLIER`, for example `BRKT-9:A@ACME`.
   - The states are Required, NotRequired, NotApplicable and PendingDetermination. No determination counts as
     pending.
   - The latest determination is in force; earlier ones are history.
2. **FAI records** (`quality.fai`, append-only) hold structured results (characteristic, requirement, measured,
   pass) and name the FAI report. The report must be a released revision of a controlled document: the release
   approval is its signature.
   - A passing FAI cannot have a failed characteristic.
   - FAI status combines the two: NotApplicable (no supplier), NotRequired, PendingDetermination, Required (no passing
     FAI yet) and Complete.
   - Only NotApplicable, NotRequired and Complete allow acceptance. Pending determination is never complete.
3. **Receiving inspection** (`inventory.receiving_inspection`, append-only) inspects all or part of a receipt into
   inspection.
   - Accepted quantity is released and rejected quantity goes on hold; the rest stays in inspection for a later
     inspection.
   - Each inspection keeps its results and the FAI status it found.
   - It refuses: acceptance when FAI does not allow it; more than is left to inspect; a failed characteristic with
     nothing rejected; a rejection with no defect; and the person who received the material inspecting it.
   - The supplier comes from the purchase order.
4. **Supplier dispositions** (`inventory.supplier_rejection`) are recorded with a reason by quality disposition
   authority. The choices are:
   - return to vendor, which opens an RTV;
   - rework by the supplier, which also opens an RTV, marked for rework;
   - scrap, which posts a scrap transaction from hold;
   - use as is, which waits until a different person with quality approval approves it and only then releases.
5. **Return to vendor** (`inventory.rtv`) goes Open → Shipped → Replaced or Credited.
   - Shipping posts a new ledger kind, `ReturnToVendor`, out of hold.
   - Replacement material is received under a new lot (the old lot number is refused) and linked to the returned lot
     with a new instance relation, `Replaces`. Lot lineage and the genealogy graph show it.
   - The returned lot keeps its whole history.
   - A credit closes the RTV instead. It needs purchasing approval and a reason.
   - Receiving a replacement checks the Receive holds of V3.10.
6. **Customer returns** (`inventory.customer_return`) are received with the customer's reason against a shipped
   shipment, onto hold, through a new ledger kind, `CustomerReturn`. The returned quantity can never exceed what the
   shipment carried.
   - Dispositions: restock (released), rework (stays on hold) or scrap.
7. **Visibility.**
   - The Inventory page has a Receiving tab.
   - The `receiving-inspection` report has an FAI status filter.
   - The Analyst answers "Which receipts are waiting for inspection?" and "Which FAIs are pending determination?".

## Consequences
- Migrations `quality/0005` and `inventory/0013` add the tables. They extend the ledger kinds (`ReturnToVendor`,
  `CustomerReturn`) and the instance relations (`Replaces`). `GenealogyRelation` gains `Replaced`.
- The whole-quantity release from inspection (`/api/inventory/releases`) stays for material that needs no
  inspection record. Receiving inspection is the path that records results and FAI.

## Codex review corrections

- Receiving separation of duties follows the actor on the exact receipt, even after another receipt changes the stock's latest actor.
- RTV replacements must use a lot identity not already present for the part, including unrelated lots.
- Customer-return quantity checks run under the existing per-part advisory transaction lock; concurrent returns cannot exceed the shipped quantity.
- FAI recording checks report document program/marking visibility; FAI list output also filters report visibility. A hidden report is reported as absent.
- Regression tests cover all four changes. Verification pending; the source remains staged until V3.10 completes its full gate.

## Verified checkpoint

Full verification PASS: Planning 266, architecture 32, kernel 33, integration 90. Release build zero warnings/errors. Focused V311 API retry passed 5/5; saved verify-v311.* and codex-v311-fix.*.

The initial API gate passed 12/14 and exposed two package defects: the old shipment-reference constraint excluded customer returns, and use-as-is ledger approval wrote the approver as both actors. The migration now permits return/disposition shipment references; the ledger preserves the original disposition actor and distinct approver. Replacement receipts retain their original PO/supplier, require quantity reconciliation and a new instance. Mutable transitions reload under the existing part lock. Existing package naming/analyzer collisions were corrected without weakening checks. FAI reference evidence replaces the temporary controlled-report route in V3.12 while historical V3.11 records remain preserved.
