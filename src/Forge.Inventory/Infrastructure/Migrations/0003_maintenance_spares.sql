-- Spare parts issued to (and returned from) maintenance work orders go through the ledger like any stock.
ALTER TABLE inventory.transaction ADD COLUMN work_order_id bigint NULL REFERENCES maintenance.work_order(work_order_id);

ALTER TABLE inventory.transaction DROP CONSTRAINT transaction_kind_check;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_kind_check
    CHECK (kind IN ('Receipt', 'Issue', 'Return', 'Transfer', 'Adjustment', 'ProductionReceipt', 'Scrap', 'CycleCount', 'QualityHold', 'QualityRelease', 'Shipment',
                    'MaintenanceIssue', 'MaintenanceReturn'));
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_work_order_check
    CHECK ((kind IN ('MaintenanceIssue', 'MaintenanceReturn')) = (work_order_id IS NOT NULL));
CREATE INDEX transaction_work_order ON inventory.transaction (work_order_id) WHERE work_order_id IS NOT NULL;
