UPDATE kernel.notification_rule
SET clear_event_type = 'MaterialCustodyResolved',
    resolution_template = 'The material handoff was fully accepted or rejected into controlled quarantine.'
WHERE rule_id = '0b1d5a57-0010-4a02-9a02-000000000012';

INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0010-4a02-9a02-000000000013','Material delivery rejected quantity needs review',true,'MaterialCustodyDeliveryDiscrepancy','MaterialCustodyResolved','Global',NULL,NULL,NULL,
     'Critical','Confirmed',ARRAY['InventoryController','QualityEngineer','QualityManager','Planner'],ARRAY['Planner'],ARRAY['InApp'],
     'Task {entityId} has material rejected at receipt. The rejected quantity is held in non-nettable quarantine; review the linked custody and quality evidence.',
     'The handoff quantities were fully settled and the exception alert was resolved.','#/inventory?tab=tasks',0,1800,3600,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
