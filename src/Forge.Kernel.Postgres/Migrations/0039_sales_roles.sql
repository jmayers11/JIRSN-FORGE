-- Owner decisions 2026-10-31 (quote to cash): a Sales Representative keeps customers and drafts quotes and sales orders; a Sales
-- Manager, never the author, approves them; a Billing Clerk invoices shipped quantities and exports them, with the Cost Accountant,
-- for the accounting system. Sales managers see every area like the other managers; every manager can read sales.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('sales.read', 'Read customers, quotes, sales orders and invoices'),
    ('sales.manage', 'Keep customers and draft quotes and sales orders'),
    ('sales.approve', 'Approve quotes and sales orders written by someone else and put customers on hold'),
    ('sales.billing.manage', 'Issue and void invoices of shipped quantities'),
    ('accounting.export', 'Export invoices and journal entries for the accounting system')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES
    ('SalesRepresentative', 'Sales Representative'),
    ('SalesManager', 'Sales Manager'),
    ('BillingClerk', 'Billing Clerk')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('SalesRepresentative', 'sales.read'),
    ('SalesRepresentative', 'sales.manage'),
    ('SalesRepresentative', 'engineering.read'),
    ('SalesRepresentative', 'inventory.read'),
    ('SalesRepresentative', 'planning.mrp.read'),
    ('SalesManager', 'sales.read'),
    ('SalesManager', 'sales.approve'),
    ('BillingClerk', 'sales.read'),
    ('BillingClerk', 'sales.billing.manage'),
    ('BillingClerk', 'accounting.export'),
    ('BillingClerk', 'inventory.read'),
    ('CostAccountant', 'accounting.export')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
SELECT role_name, permission
FROM unnest(ARRAY['SalesRepresentative', 'SalesManager', 'BillingClerk']) AS r(role_name)
CROSS JOIN unnest(ARRAY['notifications.read', 'notifications.acknowledge', 'kernel.delegations.propose']) AS p(permission)
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
SELECT 'SalesManager', permission
FROM unnest(ARRAY[
    'resources.read', 'workforce.read', 'competency.read', 'safety.rules.read', 'planning.readiness.read',
    'engineering.read', 'execution.orders.read', 'maintenance.read', 'planning.schedule.read', 'execution.shopfloor.read',
    'holds.read', 'inventory.read', 'procurement.read', 'quality.read', 'quality.qms.read',
    'identification.read', 'planning.mrp.read', 'programs.read', 'projects.read', 'costing.read',
    'planning.analyst.use', 'planning.reports.save']) AS p(permission)
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
SELECT role_name, 'sales.read'
FROM unnest(ARRAY['Supervisor', 'PlantManager', 'QualityManager', 'PurchasingManager', 'ProgramManager', 'FacilitiesManager']) AS r(role_name)
ON CONFLICT DO NOTHING;
