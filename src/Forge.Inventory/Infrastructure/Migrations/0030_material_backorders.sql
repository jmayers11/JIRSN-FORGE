-- Sprint 10 (WW-S10-26): when a request cannot be fully covered, the stockroom allocates what is there and the rest becomes a linked
-- Backordered task. It is still requested demand (never double-counted), is allocated when stock arrives, and planners are told.
ALTER TABLE inventory.material_task DROP CONSTRAINT material_task_status_check;
ALTER TABLE inventory.material_task ADD CONSTRAINT material_task_status_check
    CHECK (status IN ('ApprovalNeeded', 'Requested', 'Backordered', 'Allocated', 'Picked', 'InTransit', 'Delivered', 'DeliveryRejected', 'Rejected', 'Cancelled'));
ALTER TABLE inventory.material_task DROP CONSTRAINT material_task_allocated_source;
ALTER TABLE inventory.material_task ADD CONSTRAINT material_task_allocated_source
    CHECK (status IN ('ApprovalNeeded', 'Requested', 'Backordered', 'Rejected', 'Cancelled') OR from_location IS NOT NULL);
ALTER TABLE inventory.material_task
    ADD COLUMN backorder_of_task_id bigint NULL REFERENCES inventory.material_task(task_id),
    ADD CONSTRAINT material_task_backorder_link CHECK (status <> 'Backordered' OR backorder_of_task_id IS NOT NULL);

DROP INDEX inventory.material_task_open;
CREATE INDEX material_task_open ON inventory.material_task (status, part_number)
    WHERE status IN ('ApprovalNeeded', 'Requested', 'Backordered', 'Allocated', 'Picked', 'InTransit');

INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0010-4a02-9a02-000000000026','Material backordered',true,'MaterialTaskChanged','MaterialTaskClosed','Global',NULL,'status','Backordered',
     'ActionRequired','Confirmed',ARRAY['Planner','MaterialHandler'],ARRAY['Planner'],ARRAY['InApp'],
     'Material task {entityId} is backordered: stock did not cover the whole request. It is allocated when stock arrives.',
     'The backordered material task was delivered or cancelled.','#/inventory?tab=tasks',0,NULL,NULL,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
