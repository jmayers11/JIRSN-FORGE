-- V2.10: material issued to (and returned from) facility and capital projects goes through the ledger like any
-- stock, against a project material line. Receipts against a purchase line bought for a project line record
-- that line too (allocation is recorded on the receipt; it is not a reservation).
ALTER TABLE inventory.transaction ADD COLUMN project_code text NULL REFERENCES projects.project(code);
ALTER TABLE inventory.transaction ADD COLUMN project_line integer NULL;

ALTER TABLE inventory.transaction DROP CONSTRAINT transaction_kind_check;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_kind_check
    CHECK (kind IN ('Receipt', 'Issue', 'Return', 'Transfer', 'Adjustment', 'ProductionReceipt', 'Scrap', 'CycleCount', 'QualityHold', 'QualityRelease', 'Shipment',
                    'MaintenanceIssue', 'MaintenanceReturn', 'ProjectIssue', 'ProjectReturn'));
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_project_check
    CHECK ((project_code IS NULL) = (project_line IS NULL)
       AND (kind NOT IN ('ProjectIssue', 'ProjectReturn') OR project_code IS NOT NULL)
       AND (project_code IS NULL OR kind IN ('Receipt', 'ProjectIssue', 'ProjectReturn')));
CREATE INDEX transaction_project ON inventory.transaction (project_code, project_line) WHERE project_code IS NOT NULL;
