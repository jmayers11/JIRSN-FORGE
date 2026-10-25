-- Sprint 10 (WW-S10-14/29): an approved rework plan adds operations and material lines to a released order. They are marked with
-- the rework record they carry out and, for operations, the quantity that rework took back; everything else about them is an
-- ordinary order operation or released material line (schedule, readiness, labor, MRP demand). Existing lines are never changed.
ALTER TABLE execution.order_operation
    ADD COLUMN rework_id bigint NULL,
    ADD COLUMN planned_quantity numeric(18, 6) NULL CHECK (planned_quantity IS NULL OR planned_quantity > 0),
    ADD CONSTRAINT order_operation_rework_quantity CHECK ((rework_id IS NULL) = (planned_quantity IS NULL));
ALTER TABLE execution.order_material
    ADD COLUMN rework_id bigint NULL;
