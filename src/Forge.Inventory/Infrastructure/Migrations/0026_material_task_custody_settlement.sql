-- Sprint 10: separate the receiver's accepted, rejected, returned, and corrected quantities from the pick.
-- All balances still move only through canonical inventory transactions; custody events retain each decision.
ALTER TABLE inventory.material_task DROP CONSTRAINT material_task_status_check;
ALTER TABLE inventory.material_task ADD CONSTRAINT material_task_status_check
    CHECK (status IN ('ApprovalNeeded', 'Requested', 'Allocated', 'Picked', 'InTransit', 'Delivered', 'DeliveryRejected', 'Rejected', 'Cancelled'));

ALTER TABLE inventory.material_task
    ADD COLUMN received_quantity numeric(18, 6) NOT NULL DEFAULT 0 CHECK (received_quantity >= 0),
    ADD COLUMN rejected_quantity numeric(18, 6) NOT NULL DEFAULT 0 CHECK (rejected_quantity >= 0),
    ADD COLUMN returned_quantity numeric(18, 6) NOT NULL DEFAULT 0 CHECK (returned_quantity >= 0),
    ADD COLUMN reversed_quantity numeric(18, 6) NOT NULL DEFAULT 0 CHECK (reversed_quantity >= 0);

-- Existing fully delivered tasks are known to have received their picked quantity.
UPDATE inventory.material_task
SET received_quantity = COALESCE(picked_quantity, quantity)
WHERE status = 'Delivered';

ALTER TABLE inventory.material_task
    ADD CONSTRAINT material_task_custody_settlement_bounds CHECK (
        received_quantity + rejected_quantity <= COALESCE(picked_quantity, 0) AND
        returned_quantity + reversed_quantity <= received_quantity AND
        (status <> 'DeliveryRejected' OR (picked_quantity IS NOT NULL AND transaction_id IS NOT NULL)) AND
        (status <> 'Delivered' OR (received_quantity > 0 AND received_quantity + rejected_quantity = picked_quantity)) AND
        (status <> 'DeliveryRejected' OR (received_quantity = 0 AND rejected_quantity > 0 AND rejected_quantity = picked_quantity))
    );

ALTER TABLE inventory.material_task_custody_event DROP CONSTRAINT material_task_custody_event_kind_check;
ALTER TABLE inventory.material_task_custody_event ADD CONSTRAINT material_task_custody_event_kind_check
    CHECK (kind IN ('DispatchedToTransit', 'ReceivedAtDestination', 'PartiallyReceived', 'RejectedAtReceipt', 'ExceptionRecorded', 'ReturnedToStores', 'ReceiptReversed'));
ALTER TABLE inventory.material_task_custody_event
    ADD COLUMN quantity numeric(18, 6) NULL CHECK (quantity IS NULL OR quantity > 0);

DROP INDEX inventory.material_task_open;
CREATE INDEX material_task_open ON inventory.material_task (status, part_number)
    WHERE status IN ('ApprovalNeeded', 'Requested', 'Allocated', 'Picked', 'InTransit');
