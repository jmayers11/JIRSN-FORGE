-- Owner decisions 2026-11-01: hazard communication. A new EHS Coordinator keeps the chemical register and approves safety data sheets;
-- buyers, purchasing managers, manufacturing engineers and inventory controllers may upload a supplier's SDS for review. Every
-- signed-in person may read approved SDS (no permission needed). The EHS Coordinator is told when an SDS waits and when a review falls due.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('hazcom.manage', 'Keep the chemical register and approve, reject and review safety data sheets'),
    ('hazcom.submit', 'Upload a supplier''s safety data sheet for review'),
    ('hazcom.flag', 'Flag safety data sheet reviews that fall due (the system)')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('EHSCoordinator', 'EHS Coordinator')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('EHSCoordinator', 'hazcom.manage'),
    ('EHSCoordinator', 'hazcom.submit'),
    ('EHSCoordinator', 'engineering.documents.attach'),
    ('EHSCoordinator', 'engineering.read'),
    ('EHSCoordinator', 'inventory.read'),
    ('EHSCoordinator', 'procurement.read'),
    ('EHSCoordinator', 'notifications.read'),
    ('EHSCoordinator', 'notifications.acknowledge'),
    ('EHSCoordinator', 'kernel.delegations.propose'),
    ('Buyer', 'hazcom.submit'),
    ('PurchasingManager', 'hazcom.submit'),
    ('ManufacturingEngineer', 'hazcom.submit'),
    ('InventoryController', 'hazcom.submit')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0045-4a02-9a02-000000000001','Safety data sheet waiting for review',true,'SdsChanged','SdsReviewed','Global',NULL,'status','Submitted',
     'ActionRequired','Confirmed',ARRAY['EHSCoordinator'],ARRAY[]::text[],ARRAY['InApp'],
     '{message}','The safety data sheet was approved or rejected.','#/chemicals',0,86400,NULL,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0045-4a02-9a02-000000000002','Safety data sheet review due',true,'SdsReviewDue','SdsReviewed','Global',NULL,NULL,NULL,
     'Watch','Confirmed',ARRAY['EHSCoordinator'],ARRAY[]::text[],ARRAY['InApp'],
     '{message}','The safety data sheet was confirmed current or replaced.','#/chemicals',0,604800,NULL,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;

-- Owner decision 2026-11-01: HazCom training. The EHS Coordinator sees who holds it and may request it; when a chemical becomes hazardous
-- the EHS and training coordinators are asked to decide whether site training covers it. A person decides; nothing is assigned or blocked.
INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('EHSCoordinator', 'competency.read'),
    ('EHSCoordinator', 'workforce.read'),
    ('EHSCoordinator', 'workforce.training-requests.read'),
    ('EHSCoordinator', 'workforce.training-requests.request')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0045-4a02-9a02-000000000003','New hazardous chemical - review HazCom training',true,'ChemicalChanged','HazcomTrainingReviewed','Global',NULL,'newlyHazardous','true',
     'ActionRequired','Confirmed',ARRAY['EHSCoordinator','TrainingCoordinator'],ARRAY[]::text[],ARRAY['InApp'],
     '{message}','The EHS Coordinator decided whether HazCom training covers it.','#/chemicals?tab=training',0,86400,NULL,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
