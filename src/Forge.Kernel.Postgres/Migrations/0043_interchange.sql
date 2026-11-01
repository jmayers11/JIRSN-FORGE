-- Owner decisions 2026-11-01: API and EDI. An Integration Administrator keeps trading partners, API keys and webhooks; sales
-- representatives and buyers read the EDI log and reprocess or reject what Forge could not map; managers read it. The system records,
-- processes and sends documents. A document that needs review tells the integration administrator and sales representatives; a webhook
-- that gives up tells the integration administrator.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('interchange.manage', 'Keep trading partners, API keys and webhooks'),
    ('interchange.read', 'Read the EDI document log, partners, keys and webhooks (never a key or secret)'),
    ('interchange.receive', 'Receive X12 files (upload, folder, partner API)'),
    ('interchange.review', 'Reprocess or reject EDI documents that need review'),
    ('interchange.process', 'Process EDI documents and send webhooks (the system)')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('IntegrationAdministrator', 'Integration Administrator')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('IntegrationAdministrator', 'interchange.manage'),
    ('IntegrationAdministrator', 'interchange.read'),
    ('IntegrationAdministrator', 'interchange.receive'),
    ('IntegrationAdministrator', 'interchange.review'),
    ('IntegrationAdministrator', 'sales.read'),
    ('IntegrationAdministrator', 'procurement.read'),
    ('IntegrationAdministrator', 'notifications.read'),
    ('IntegrationAdministrator', 'notifications.acknowledge'),
    ('IntegrationAdministrator', 'kernel.delegations.propose'),
    ('SalesRepresentative', 'interchange.read'),
    ('SalesRepresentative', 'interchange.review'),
    ('Buyer', 'interchange.read'),
    ('Buyer', 'interchange.review'),
    ('SalesManager', 'interchange.read'),
    ('PurchasingManager', 'interchange.read')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0043-4a02-9a02-000000000001','EDI document needs review',true,'InterchangeDocumentChanged','InterchangeDocumentReviewed','Global',NULL,'status','NeedsReview',
     'ActionRequired','Confirmed',ARRAY['IntegrationAdministrator','SalesRepresentative'],ARRAY['SalesManager'],ARRAY['InApp'],
     '{message}',
     'The document was reprocessed or rejected.','#/integrations',0,14400,86400,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0043-4a02-9a02-000000000002','Webhook delivery failed',true,'WebhookDeliveryFailed',NULL,'Global',NULL,NULL,NULL,
     'ActionRequired','Confirmed',ARRAY['IntegrationAdministrator'],ARRAY[]::text[],ARRAY['InApp'],
     '{message}',
     'Acknowledged by the integration administrator.','#/integrations',0,NULL,NULL,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
