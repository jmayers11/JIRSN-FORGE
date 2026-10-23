-- In-app alerts use the existing durable notification outbox. Administrators may add Email after
-- the verified sender endpoint is configured; each transition is deduplicated by task identity.
INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0010-4a02-9a02-000000000010','Additional material request needs review',true,'MaterialTaskChanged','MaterialTaskReviewed','Global',NULL,'status','ApprovalNeeded',
     'ActionRequired','Confirmed',ARRAY['Planner','InventoryController'],ARRAY['Planner'],ARRAY['InApp'],
     'Additional material task {entityId} needs an independent decision before stockroom fulfillment.',
     'The additional material request was approved or rejected by an independent reviewer.','#/inventory?tab=tasks',0,3600,7200,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0010-4a02-9a02-000000000011','Material request awaiting stockroom fulfillment',true,'MaterialTaskChanged','MaterialTaskClosed','Global',NULL,'status','Requested',
     'Watch','Confirmed',ARRAY['MaterialHandler','Planner'],ARRAY['Planner'],ARRAY['InApp'],
     'Material task {entityId} is queued for stockroom fulfillment. Allocation, picking, and receipt remain separate steps.',
     'The material task was delivered, rejected, or cancelled.','#/inventory?tab=tasks',0,3600,7200,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0010-4a02-9a02-000000000012','Material custody exception needs reconciliation',true,'MaterialCustodyExceptionReported','MaterialCustodyAccepted','Global',NULL,NULL,NULL,
     'Critical','Confirmed',ARRAY['InventoryController','MaterialHandler','Planner'],ARRAY['Planner'],ARRAY['InApp'],
     'Material task {entityId} has a receiver-reported custody exception. Stock remains non-nettable in transit until a controlled reconciliation.',
     'The receiving worker accepted the material after the reported exception.','#/inventory?tab=tasks',0,1800,3600,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
