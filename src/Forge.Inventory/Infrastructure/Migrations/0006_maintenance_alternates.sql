-- V2.9: an approved alternate (maintenance-only ones included) issued to a maintenance work order in place
-- of the part it needs, recorded the same way as for production orders.
ALTER TABLE inventory.transaction DROP CONSTRAINT transaction_substitution_on_order;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_substitution_on_order
    CHECK (substitutes_for IS NULL OR primary_quantity > 0 AND (
        (kind IN ('Issue', 'Return') AND order_number IS NOT NULL)
        OR (kind IN ('MaintenanceIssue', 'MaintenanceReturn') AND work_order_id IS NOT NULL)));
