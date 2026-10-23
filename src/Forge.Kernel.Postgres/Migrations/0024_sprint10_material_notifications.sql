-- In-app by default; tenant administrators can enable Email after configuring the verified sender endpoint.
INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0010-4a02-9a02-000000000001','Suspect material needs Quality review',true,'SuspectScrapReported','SuspectScrapEscalated','Global',NULL,NULL,NULL,
     'ActionRequired','Confirmed',ARRAY['QualityEngineer','Inspector'],ARRAY['QualityManager'],ARRAY['InApp'],
     'Suspect material report {entityId} needs Quality review. This report is not approved scrap or an inventory transaction.',
     'The suspect report was linked to a controlled NCR.','#/quality?tab=suspect',0,86400,86400,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0010-4a02-9a02-000000000002','Material delivery awaits independent receipt',true,'MaterialTaskChanged','MaterialCustodyAccepted','Global',NULL,'status','InTransit',
     'ActionRequired','Confirmed',ARRAY['MaterialHandler','InventoryController','Supervisor','Planner'],ARRAY['Planner'],ARRAY['InApp'],
     'Material task {entityId} is in transit and awaits receiver confirmation. It is not available at the line until receipt.',
     'An independent worker accepted the material at its destination.','#/inventory?tab=tasks',0,3600,3600,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
