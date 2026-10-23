ALTER TABLE inventory.material_task DROP CONSTRAINT material_task_status_check;
ALTER TABLE inventory.material_task ADD CONSTRAINT material_task_status_check
    CHECK (status IN ('ApprovalNeeded', 'Requested', 'Allocated', 'Picked', 'InTransit', 'Delivered', 'Rejected', 'Cancelled'));

ALTER TABLE inventory.material_task
    ADD COLUMN is_non_bom boolean NOT NULL DEFAULT false,
    ADD COLUMN reviewed_by text NULL,
    ADD COLUMN reviewed_at timestamptz NULL,
    ADD COLUMN review_note text NULL,
    ADD CONSTRAINT material_task_non_bom_review_state CHECK (
        (status <> 'ApprovalNeeded' OR (is_non_bom AND reviewed_by IS NULL AND reviewed_at IS NULL)) AND
        (status <> 'Requested' OR NOT is_non_bom OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL)) AND
        (status <> 'Rejected' OR (is_non_bom AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND review_note IS NOT NULL))
    );

DROP INDEX inventory.material_task_open;
CREATE INDEX material_task_open ON inventory.material_task (status, part_number)
    WHERE status IN ('ApprovalNeeded', 'Requested', 'Allocated', 'Picked', 'InTransit');
