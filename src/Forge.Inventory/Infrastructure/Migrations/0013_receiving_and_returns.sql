-- V3.11: receiving inspection, supplier dispositions, return to vendor and customer returns (V3 brief O, P, R, AW).
--
-- A receipt into inspection is inspected, possibly more than once: part of it accepted (released), part rejected (to
-- hold), the rest still waiting. Each inspection keeps its structured results and the FAI status it found. A rejected
-- quantity gets a supplier disposition: return to vendor, rework by the supplier, scrap, or use as is (a second person
-- approves). A return to vendor ships the held material back; replacement material is received as a new instance
-- linked to the one it replaces, which keeps its history. A customer return comes back against a shipment, onto hold,
-- and is dispositioned.

ALTER TABLE inventory.transaction DROP CONSTRAINT transaction_kind_check;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_kind_check
    CHECK (kind IN ('Receipt', 'Issue', 'Return', 'Transfer', 'Adjustment', 'ProductionReceipt', 'Scrap', 'CycleCount', 'QualityHold', 'QualityRelease', 'Shipment',
                    'MaintenanceIssue', 'MaintenanceReturn', 'ProjectIssue', 'ProjectReturn', 'Split', 'Merge', 'Remnant', 'Byproduct', 'ReturnToVendor', 'CustomerReturn'));

ALTER TABLE inventory.transaction DROP CONSTRAINT transaction_shipment_check;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_shipment_check CHECK
    ((kind IN ('Shipment', 'CustomerReturn') AND shipment_id IS NOT NULL)
     OR (kind IN ('QualityRelease', 'Scrap'))
     OR (kind NOT IN ('Shipment', 'CustomerReturn', 'QualityRelease', 'Scrap') AND shipment_id IS NULL));

ALTER TABLE inventory.instance_link DROP CONSTRAINT instance_link_relation_check;
ALTER TABLE inventory.instance_link ADD CONSTRAINT instance_link_relation_check CHECK (relation IN ('Split', 'Merge', 'Replaces'));

CREATE TABLE inventory.receiving_inspection (
    inspection_id           bigint GENERATED ALWAYS AS IDENTITY (START WITH 7001) PRIMARY KEY,
    receipt_transaction_id  bigint NOT NULL REFERENCES inventory.transaction(transaction_id),
    part_number             text NOT NULL,
    lot_number              text NULL,
    location_code           text NOT NULL,
    po_number               bigint NULL,
    supplier_code           text NULL,
    accepted                numeric(18, 6) NOT NULL CHECK (accepted >= 0),
    rejected                numeric(18, 6) NOT NULL CHECK (rejected >= 0),
    results                 jsonb NOT NULL,
    fai_status              text NOT NULL CHECK (fai_status IN ('NotApplicable', 'NotRequired', 'PendingDetermination', 'Required', 'Complete')),
    inspector               text NOT NULL,
    at                      timestamptz NOT NULL,
    CHECK (accepted + rejected > 0)
);
CREATE INDEX receiving_inspection_receipt ON inventory.receiving_inspection (receipt_transaction_id);

CREATE TABLE inventory.supplier_rejection (
    rejection_id     bigint GENERATED ALWAYS AS IDENTITY (START WITH 8001) PRIMARY KEY,
    inspection_id    bigint NOT NULL REFERENCES inventory.receiving_inspection(inspection_id),
    part_number      text NOT NULL,
    lot_number       text NULL,
    location_code    text NOT NULL,
    supplier_code    text NULL,
    po_number        bigint NULL,
    quantity         numeric(18, 6) NOT NULL CHECK (quantity > 0),
    defect           text NOT NULL,
    disposition      text NOT NULL CHECK (disposition IN ('Pending', 'ReturnToVendor', 'ReworkBySupplier', 'Scrap', 'UseAsIs')),
    dispositioned_by text NULL,
    dispositioned_at timestamptz NULL,
    approved_by      text NULL,
    approved_at      timestamptz NULL,
    CHECK ((disposition = 'Pending') = (dispositioned_by IS NULL)),
    CHECK (approved_by IS NULL OR approved_by <> dispositioned_by)
);

CREATE TABLE inventory.rtv (
    rtv_id                   bigint GENERATED ALWAYS AS IDENTITY (START WITH 9001) PRIMARY KEY,
    rejection_id             bigint NOT NULL REFERENCES inventory.supplier_rejection(rejection_id),
    supplier_code            text NULL,
    part_number              text NOT NULL,
    lot_number               text NULL,
    location_code            text NOT NULL,
    quantity                 numeric(18, 6) NOT NULL CHECK (quantity > 0),
    for_rework               boolean NOT NULL,
    status                   text NOT NULL CHECK (status IN ('Open', 'Shipped', 'Replaced', 'Credited')),
    shipped_transaction_id   bigint NULL REFERENCES inventory.transaction(transaction_id),
    shipped_by               text NULL,
    shipped_at               timestamptz NULL,
    replacement_lot          text NULL,
    replacement_transaction_id bigint NULL REFERENCES inventory.transaction(transaction_id),
    closed_by                text NULL,
    closed_at                timestamptz NULL,
    note                     text NULL,
    CHECK ((status = 'Open') = (shipped_transaction_id IS NULL)),
    CHECK (status NOT IN ('Replaced', 'Credited') OR closed_by IS NOT NULL)
);

CREATE TABLE inventory.customer_return (
    rma_id                  bigint GENERATED ALWAYS AS IDENTITY (START WITH 6001) PRIMARY KEY,
    shipment_id             bigint NOT NULL REFERENCES inventory.shipment(shipment_id),
    customer                text NOT NULL,
    part_number             text NOT NULL,
    lot_number              text NULL,
    quantity                numeric(18, 6) NOT NULL CHECK (quantity > 0),
    location_code           text NOT NULL,
    reason                  text NOT NULL,
    receipt_transaction_id  bigint NOT NULL REFERENCES inventory.transaction(transaction_id),
    received_by             text NOT NULL,
    received_at             timestamptz NOT NULL,
    disposition             text NULL CHECK (disposition IN ('Restock', 'Rework', 'Scrap')),
    dispositioned_by        text NULL,
    dispositioned_at        timestamptz NULL,
    disposition_transaction_id bigint NULL REFERENCES inventory.transaction(transaction_id),
    CHECK ((disposition IS NULL) = (dispositioned_by IS NULL))
);

-- Inspections are records; rejections, RTVs and returns only move forward (checked in the service).
CREATE TRIGGER receiving_inspection_append_only BEFORE UPDATE OR DELETE ON inventory.receiving_inspection
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
