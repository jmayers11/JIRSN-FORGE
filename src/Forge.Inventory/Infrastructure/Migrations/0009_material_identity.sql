-- V3.2: material identity and quantity (V3 brief D, E, K).
--
-- A lot row is a physical instance of a part: a lot, batch, roll, serial, reel, sheet or piece. Its quantity is
-- always the ledger sum; moving it never changes its identity. Splitting it creates new instances whose quantities
-- add up to what left the parent; merging (only where policy allows) creates or adds to one instance. Each split
-- or merge is recorded as a link between the instances, kept forever (the start of the genealogy graph, V3.3).

ALTER TABLE inventory.lot ADD COLUMN identity_kind text NOT NULL DEFAULT 'Lot'
    CHECK (identity_kind IN ('Lot', 'Batch', 'Roll', 'Serial', 'Reel', 'Sheet', 'Piece'));
UPDATE inventory.lot l SET identity_kind = 'Serial'
FROM engineering.item i WHERE i.part_number = l.part_number AND i.serialized;

ALTER TABLE inventory.transaction DROP CONSTRAINT transaction_kind_check;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_kind_check
    CHECK (kind IN ('Receipt', 'Issue', 'Return', 'Transfer', 'Adjustment', 'ProductionReceipt', 'Scrap', 'CycleCount', 'QualityHold', 'QualityRelease', 'Shipment',
                    'MaintenanceIssue', 'MaintenanceReturn', 'ProjectIssue', 'ProjectReturn', 'Split', 'Merge'));

CREATE TABLE inventory.instance_link (
    link_id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    relation            text NOT NULL CHECK (relation IN ('Split', 'Merge')),
    source_part         text NOT NULL,
    source_lot          text NOT NULL,
    result_part         text NOT NULL,
    result_lot          text NOT NULL,
    quantity            numeric(18, 6) NOT NULL CHECK (quantity > 0),
    out_transaction_id  bigint NOT NULL REFERENCES inventory.transaction(transaction_id),
    in_transaction_id   bigint NOT NULL REFERENCES inventory.transaction(transaction_id),
    reason              text NULL,
    at                  timestamptz NOT NULL,
    actor               text NOT NULL,
    FOREIGN KEY (source_part, source_lot) REFERENCES inventory.lot(part_number, lot_number),
    FOREIGN KEY (result_part, result_lot) REFERENCES inventory.lot(part_number, lot_number),
    CHECK (source_part <> result_part OR source_lot <> result_lot)
);
CREATE INDEX instance_link_source ON inventory.instance_link (source_part, source_lot);
CREATE INDEX instance_link_result ON inventory.instance_link (result_part, result_lot);

CREATE TRIGGER instance_link_append_only BEFORE UPDATE OR DELETE ON inventory.instance_link
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();

-- Merging lots loses the separate identity of each source, so it is off for every part until someone with
-- inventory approval authority allows it, with the conditions the sources must share. Every setting is kept.
CREATE TABLE inventory.merge_policy (
    policy_id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number                text NOT NULL REFERENCES engineering.item(part_number),
    allowed                    boolean NOT NULL,
    same_revision_required     boolean NOT NULL,
    same_supplier_lot_required boolean NOT NULL,
    reason                     text NOT NULL,
    set_by                     text NOT NULL,
    set_at                     timestamptz NOT NULL
);
CREATE INDEX merge_policy_part ON inventory.merge_policy (part_number, policy_id DESC);
CREATE TRIGGER merge_policy_append_only BEFORE UPDATE OR DELETE ON inventory.merge_policy
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
