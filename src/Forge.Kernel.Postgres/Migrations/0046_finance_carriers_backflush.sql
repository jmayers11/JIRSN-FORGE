-- Owner decisions 2026-11-01: features a company can turn off; customer payments and credit holds (new Credit Manager role); supplier
-- invoices with the three-way match (new Accounts Payable Clerk role); shipping carriers; automatic material deduction (backflush).
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('site.features.manage', 'Turn the optional parts of Forge on or off for the plant'),
    ('sales.payments.record', 'Record and void customer payments'),
    ('sales.credit.manage', 'Set customer credit limits and the credit policy'),
    ('sales.credit.release', 'Release a sales order or shipment from a customer''s credit hold'),
    ('procurement.invoices.enter', 'Enter, void and mark paid supplier invoices'),
    ('procurement.invoices.approve', 'Decide supplier invoices outside the three-way match tolerance, and set the tolerance'),
    ('inventory.carriers.manage', 'Set up shipping carrier accounts and their credentials'),
    ('inventory.carriers.track', 'Record carrier tracking (the system)'),
    ('inventory.backflush.record', 'Record what a backflush could not deduct (the system)')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('CreditManager', 'Credit Manager'), ('AccountsPayableClerk', 'Accounts Payable Clerk')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('SecurityAdministrator', 'site.features.manage'),
    ('BillingClerk', 'sales.payments.record'),
    ('SalesManager', 'sales.credit.release'),
    ('PurchasingManager', 'procurement.invoices.approve'),
    ('IntegrationAdministrator', 'inventory.carriers.manage'),
    ('IntegrationAdministrator', 'inventory.read'),
    ('CreditManager', 'sales.read'),
    ('CreditManager', 'sales.payments.record'),
    ('CreditManager', 'sales.credit.manage'),
    ('CreditManager', 'sales.credit.release'),
    ('CreditManager', 'inventory.read'),
    ('CreditManager', 'accounting.export'),
    ('CreditManager', 'notifications.read'),
    ('CreditManager', 'notifications.acknowledge'),
    ('CreditManager', 'kernel.delegations.propose'),
    ('AccountsPayableClerk', 'procurement.read'),
    ('AccountsPayableClerk', 'procurement.invoices.enter'),
    ('AccountsPayableClerk', 'inventory.read'),
    ('AccountsPayableClerk', 'engineering.read'),
    ('AccountsPayableClerk', 'accounting.export'),
    ('AccountsPayableClerk', 'notifications.read'),
    ('AccountsPayableClerk', 'notifications.acknowledge'),
    ('AccountsPayableClerk', 'kernel.delegations.propose')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0046-4a02-9a02-000000000001','Backflush short',true,'BackflushShortage','BackflushExceptionResolved','Global',NULL,NULL,NULL,
     'ActionRequired','Confirmed',ARRAY['InventoryController'],ARRAY[]::text[],ARRAY['InApp'],
     '{message}','The backflush shortage was resolved.','#/backflush',0,86400,NULL,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0046-4a02-9a02-000000000002','Supplier invoice outside tolerance',true,'SupplierInvoiceChanged','SupplierInvoiceDecided','Global',NULL,'status','Exception',
     'ActionRequired','Confirmed',ARRAY['PurchasingManager'],ARRAY[]::text[],ARRAY['InApp'],
     '{message}','The supplier invoice was approved, rejected or voided.','#/payables',0,86400,NULL,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration'),
    ('0b1d5a57-0046-4a02-9a02-000000000003','Carrier delivery problem',true,'CarrierException','CarrierExceptionCleared','Global',NULL,NULL,NULL,
     'ActionRequired','Confirmed',ARRAY['Shipper'],ARRAY[]::text[],ARRAY['InApp'],
     '{message}','The shipment is moving again or was delivered.','#/carriers',0,86400,NULL,NULL,NULL,'UTC',3600,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
