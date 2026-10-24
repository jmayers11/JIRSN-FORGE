-- Sprint 10 §20: planners and inventory control may ask for inventory to be scrapped. Approval (inventory.approve),
-- Quality disposition (quality.disposition) and execution (inventory.transact) remain separate, existing rights.
INSERT INTO kernel.permission_catalog(permission)
VALUES ('inventory.scrap.request')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('Planner', 'inventory.scrap.request'),
    ('InventoryController', 'inventory.scrap.request')
ON CONFLICT DO NOTHING;

-- In-app alerts through the durable notification outbox; each request opens at most one alert per rule.
INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0020-4a02-9a02-000000000020','Scrap request needs inventory approval',true,'ScrapRequested','ScrapDecided','Global',NULL,'state','PendingApproval',
     'ActionRequired','Confirmed',ARRAY['InventoryController'],ARRAY['InventoryController'],ARRAY['InApp'],
     'Scrap request {entityId} needs an independent Inventory decision before anything is scrapped.',
     'The scrap request was approved, rejected or withdrawn.','#/inventory?tab=scrap',0,3600,14400,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0020-4a02-9a02-000000000021','Scrap request needs Quality disposition',true,'ScrapRequested','ScrapDecided','Global',NULL,'state','PendingDisposition',
     'ActionRequired','Confirmed',ARRAY['QualityEngineer'],ARRAY['QualityManager'],ARRAY['InApp'],
     'Scrap request {entityId} covers quality-held or quality-related material. It stays on hold until an authorized Quality disposition.',
     'Quality decided the scrap request, or it was withdrawn.','#/inventory?tab=scrap',0,3600,14400,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0020-4a02-9a02-000000000022','Approved scrap awaiting execution',true,'ScrapDecided','ScrapExecuted','Global',NULL,'state','Approved',
     'Watch','Confirmed',ARRAY['InventoryController','MaterialHandler'],ARRAY['InventoryController'],ARRAY['InApp'],
     'Scrap request {entityId} is approved. Execute it as a separate, authorized step; stock is unchanged until then.',
     'The approved scrap was executed.','#/inventory?tab=scrap',0,7200,28800,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0020-4a02-9a02-000000000023','Inventory scrapped: review material coverage',true,'ScrapExecuted',NULL,'Global',NULL,NULL,NULL,
     'Watch','Confirmed',ARRAY['Planner','Supervisor'],ARRAY['Planner'],ARRAY['InApp'],
     'Scrap request {entityId} was posted. MRP, material readiness and the schedule now net the lower stock; review affected orders and recovery options.',
     'Scrap postings stay in the inventory ledger; this notice needs no further action.','#/inventory?tab=scrap',0,NULL,NULL,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
