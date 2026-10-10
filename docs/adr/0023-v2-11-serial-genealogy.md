# ADR 0023 — V2.11: serial numbers, the as-built record and genealogy

## Context
V2 §36, review scenario 10: months after serial 000481 shipped, someone asks what it is made of — each
component's revision, lot or serial, whether it was an alternate and who approved it, the supplier and purchase
order, the documents in force, who built it with which qualifications, the inspections, NCRs and the shipment.
The answer must come from what was recorded then. Rebuilding it from today's item revisions, alternates or
supplier records would be wrong as soon as any of them changes.

## Decisions
1. **Serial numbers** apply only to items flagged Serialized. In the ledger a serial is a lot of quantity
   one: every movement of a serialized part names its serial and moves one unit. Serials of made parts are
   assigned at production receipt, one per unit (`ReceiveFromProduction.Serials`); bought serials are recorded
   at receipt. A serial is never reused for the same part, even after the unit has left.
2. **The as-built record** (`execution.as_built`, owned by Execution) gets one line per issue to, or return
   from, a production order. Inventory appends it through the `IAsBuiltRecord` port, in the same transaction
   as the ledger entry. Each line keeps what was true at that moment:
   - the component revision and lot or serial;
   - the approved alternate, with its kind, conditions, approver and date;
   - the supplier name and code, purchase order and line, and receipt date of the lot.

   The line is append-only (database trigger). Issues made before V2.11 are counted and the genealogy says
   they are missing; they are not reconstructed.
3. **Labor keeps qualifications.** When someone starts an operation, the qualifications checked for it are
   stored with the labor record:
   - for an operator, those the operation and machine required;
   - for a trainee, those already held;
   - for a trainer, those being supervised.

   They never change afterwards (trigger).
4. **Genealogy** (`GenealogyService`) reads records only. A serial's production receipt gives its order and
   revision. The order's as-built lines give its components; components made in-house lead to their own
   orders, five levels deep. Labor, inspections, NCRs and shipments are read as recorded. Documents are those
   in force when the order was released, read from revision effective dates. A component of an asset traces
   through the issue it was installed from (`asset_component.source_transaction_id`) to its lot, supplier and
   purchase order.
5. **Program segregation is kept.** Orders of programs the reader cannot see are not shown, including as
   children, and the genealogy says so.

## Consequences
- An order of several units has one as-built record. A serial's genealogy shows its order's components, not a
  per-unit allocation, because none was recorded.
- Documents at release are worked out from effective-dated history, not from a snapshot. A rule withdrawn later
  would change the answer, and the genealogy notes this.
