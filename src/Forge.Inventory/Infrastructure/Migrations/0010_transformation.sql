-- V3.3: transformation and genealogy (V3 brief C, E, F, G, J).
--
-- Material issued to a production order is accounted for by transformation records: how much of each issued lot
-- was consumed, scrapped, lost or came back as a remnant (a new instance of the same part), what byproducts
-- the order made, and how much is still at the work center. Issued = consumed + scrap + loss + remnant +
-- remaining, within the part's yield tolerance; anything else is unexplained and stays flagged until a later
-- record explains it. Nothing is ever scrapped to make the numbers add up. Every record is kept as written.

ALTER TABLE inventory.transaction DROP CONSTRAINT transaction_kind_check;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_kind_check
    CHECK (kind IN ('Receipt', 'Issue', 'Return', 'Transfer', 'Adjustment', 'ProductionReceipt', 'Scrap', 'CycleCount', 'QualityHold', 'QualityRelease', 'Shipment',
                    'MaintenanceIssue', 'MaintenanceReturn', 'ProjectIssue', 'ProjectReturn', 'Split', 'Merge', 'Remnant', 'Byproduct'));

CREATE TABLE inventory.transformation (
    transformation_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number        bigint NOT NULL,
    operation_sequence  int NULL,
    reason              text NULL,
    recorded_at         timestamptz NOT NULL,
    actor               text NOT NULL
);
CREATE INDEX transformation_order ON inventory.transformation (order_number, transformation_id);

-- One line per quantity accounted for. Consumed, Scrap, Loss and Remaining name the issued part and lot they
-- account for; Remnant names the new instance and the issued lot it was cut from; Byproduct names the new
-- instance of another part the order made.
CREATE TABLE inventory.transformation_line (
    transformation_id   bigint NOT NULL REFERENCES inventory.transformation(transformation_id),
    line_number         int NOT NULL,
    role                text NOT NULL CHECK (role IN ('Consumed', 'Scrap', 'Loss', 'Remaining', 'Remnant', 'Byproduct')),
    part_number         text NOT NULL,
    lot_number          text NULL,
    source_part         text NULL,
    source_lot          text NULL,
    quantity            numeric(18, 6) NOT NULL CHECK (quantity >= 0),
    location_code       text NULL,
    transaction_id      bigint NULL REFERENCES inventory.transaction(transaction_id),
    PRIMARY KEY (transformation_id, line_number),
    CHECK (role IN ('Remnant', 'Byproduct') = (location_code IS NOT NULL AND transaction_id IS NOT NULL)),
    CHECK (role <> 'Remnant' OR source_part = part_number),
    CHECK (role = 'Byproduct' OR role = 'Remnant' OR source_part IS NULL),
    CHECK (role = 'Remaining' OR quantity > 0)
);
CREATE INDEX transformation_line_instance ON inventory.transformation_line (part_number, lot_number);
CREATE INDEX transformation_line_source ON inventory.transformation_line (source_part, source_lot);

CREATE TRIGGER transformation_append_only BEFORE UPDATE OR DELETE ON inventory.transformation
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
CREATE TRIGGER transformation_line_append_only BEFORE UPDATE OR DELETE ON inventory.transformation_line
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();

-- How far issued and accounted may differ before the difference is unexplained, as a percent of what was
-- issued. Absent = 0. Set with a reason; every setting is kept.
CREATE TABLE inventory.yield_tolerance (
    tolerance_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number    text NOT NULL REFERENCES engineering.item(part_number),
    percent        numeric(7, 4) NOT NULL CHECK (percent >= 0 AND percent <= 50),
    reason         text NOT NULL,
    set_by         text NOT NULL,
    set_at         timestamptz NOT NULL
);
CREATE INDEX yield_tolerance_part ON inventory.yield_tolerance (part_number, tolerance_id DESC);
CREATE TRIGGER yield_tolerance_append_only BEFORE UPDATE OR DELETE ON inventory.yield_tolerance
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
