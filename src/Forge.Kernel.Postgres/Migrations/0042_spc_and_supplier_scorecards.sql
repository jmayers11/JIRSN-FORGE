-- Owner decisions 2026-11-01: statistical process control and supplier scorecards. A Quality Manager keeps the SPC policy (required Cpk)
-- and marks characteristics critical; quality engineers are told when a new measurement breaks a control-chart rule and acknowledge it,
-- with the Quality Manager as escalation. Nothing holds work, material or suppliers automatically; a person decides.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('quality.spc.manage', 'Set the SPC policy (required Cpk) and mark characteristics critical'),
    ('quality.spc.record', 'Record control-chart signals (the system, after an inspection)')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('QualityManager', 'quality.spc.manage')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0042-4a02-9a02-000000000001','Control chart signal',true,'SpcSignalDetected','SpcSignalAcknowledged','Global',NULL,NULL,NULL,
     'ActionRequired','Confirmed',ARRAY['QualityEngineer'],ARRAY['QualityManager'],ARRAY['InApp'],
     '{message}',
     'A quality engineer acknowledged the signal.','#/spc',0,14400,86400,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
