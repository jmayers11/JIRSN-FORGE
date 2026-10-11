# ADR 0038 — V3.14 outside processing, packaging and shipping

Status: accepted; full verification passed 2026-10-11.

Outside processing is a custody movement in the Inventory ledger, not a shipment or a sale. Dispatch and return keep the company's stock balance and original lot identity. Dispatch requires an active ExternalProcessor location whose custodian matches the supplier on an approved purchase order with an open Service-class line. Returned material is transferred into Inspection status. It remains unavailable for production or customer shipment until Quality releases it through the existing inspection and disposition controls. Returned quantities are bounded by the dispatches for the same part, lot, purchase order and line.

Processor certificates and other business records use the reference evidence model under an OutsideProcess target derived from those immutable ledger transactions. Controlled engineering instructions remain separate from business evidence.

Packaging materials use the bought `Packaging` item class. A shipment can opt into a packaging requirement; the pack step then needs at least one Packaging-class line and validates its stock with all other shipment contents. The default remains false for legacy and shipments whose product policy does not require packaging.

Cartons, crates and pallets have stable codes and contain explicit part, quantity, location, status and lot identities. Packing reserves stock without changing the inventory ledger. A part/location/lot/status quantity cannot be reserved in multiple containers beyond its released balance. Sealing makes content immutable. A shipment may reference one sealed container only when its released contents exactly match the shipment lines. Shipping updates the ledger and container lifecycle in one transaction; shipped contents stop reserving on-hand stock. A failed/cancelled shipment leaves an unassigned sealed container available for a later shipment.

Partial fulfillment continues to use multiple shipments against one open demand. Customer returns remain the controlled correction path and are bounded by the shipped quantity; returned material enters hold/disposition rather than rewriting the shipment ledger.

## Architecture impact

| Aspect | Decision |
| --- | --- |
| Owners | Inventory owns identity, custody, quantity and physical containers; Procurement owns processor service POs; Quality owns acceptance; Engineering owns item class; Planning aggregates partial shipments |
| Reuse | Existing stock ledger, supplier/PO records, location custody, business evidence, shipment and customer-return models |
| Data | Three additive Inventory migrations for package policy, containers, and shipment/container association; two Engineering migrations for Packaging class and OutsideProcess evidence targets |
| Authorization | Existing Inventory transact/manage/read, procurement approval and document-evidence permissions; supplier, PO, line and custodian are checked on each processor command |
| Corrections | New ledger movements and customer returns preserve history; no edits to shipped quantities or historical lot identity |
| Validation | `V314Tests` exercises dispatch, partial return, inspection status, supplier certificate, packaging block, container reservation/seal/shipment, return overage rejection and two partial shipments fulfilling a demand |

Full verification: Release build 0 warnings/errors; Planning 266, architecture 32, kernel 33, integration 95; all passed. Focused V3.14 PostgreSQL test 1/1. Migration idempotency expected count updated from 72 to 77 for the five additive migrations.
