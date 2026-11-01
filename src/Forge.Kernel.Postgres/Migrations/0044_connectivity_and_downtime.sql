-- Owner decisions 2026-11-01: machine connections a plant chooses per machine, downtime reported at the terminal, and OEE per shift.
-- Manufacturing engineers and maintenance planners choose connections; supervisors and maintenance technicians see them; the terminal
-- reports downtime and can ask maintenance to come; a stop detected from a machine tells the supervisor it needs a reason.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('connectivity.manage', 'Choose how each machine reports to Forge'),
    ('connectivity.read', 'See machine connections and what machines report'),
    ('connectivity.record', 'Record what machine connections report (the system)')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('ManufacturingEngineer', 'connectivity.manage'),
    ('ManufacturingEngineer', 'connectivity.read'),
    ('MaintenancePlanner', 'connectivity.manage'),
    ('MaintenancePlanner', 'connectivity.read'),
    ('Supervisor', 'connectivity.read'),
    ('MaintenanceTechnician', 'connectivity.read'),
    ('ShopFloorTerminal', 'maintenance.downtime.report')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0044-4a02-9a02-000000000001','Machine stop needs a reason',true,'StoppageStarted','StoppageReclassified','Global',NULL,'reasonCode','UNEXPLAINED',
     'Watch','Confirmed',ARRAY['Supervisor'],ARRAY[]::text[],ARRAY['InApp'],
     'A machine stopped and its connection recorded it; give the stop a reason.',
     'The stop was given a reason.','#/oee',0,3600,NULL,NULL,NULL,'UTC',600,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
