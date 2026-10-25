-- Sprint 10 additional (non-BOM) material: a request waits for independent review, and a rejected one is closed, before anything
-- is allocated. The original V3.4 rule allowed an unallocated task only while Requested or Cancelled, so PostgreSQL refused every
-- additional-material request (ApprovalNeeded, no source location) and every rejection. Only allocated work needs a source location.
ALTER TABLE inventory.material_task DROP CONSTRAINT material_task_check2;
ALTER TABLE inventory.material_task ADD CONSTRAINT material_task_allocated_source
    CHECK (status IN ('ApprovalNeeded', 'Requested', 'Rejected', 'Cancelled') OR from_location IS NOT NULL);
