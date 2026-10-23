INSERT INTO kernel.permission_catalog(permission)
VALUES
    ('workforce.attendance-intelligence.read'),
    ('workforce.attendance-schedule.manage'),
    ('workforce.attendance-policy.manage'),
    ('workforce.attendance-classification.record'),
    ('workforce.attendance-classification.review'),
    ('inventory.material-request'),
    ('inventory.custody.receive'),
    ('quality.suspect.report')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('HumanResources', 'Human Resources')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('SecurityAdministrator', 'workforce.attendance-intelligence.read'),
    ('SecurityAdministrator', 'workforce.attendance-schedule.manage'),
    ('SecurityAdministrator', 'workforce.attendance-policy.manage'),
    ('SecurityAdministrator', 'workforce.attendance-classification.record'),
    ('SecurityAdministrator', 'workforce.attendance-classification.review'),
    ('HumanResources', 'kernel.people.read'),
    ('HumanResources', 'workforce.read'),
    ('HumanResources', 'workforce.attendance-intelligence.read'),
    ('HumanResources', 'workforce.attendance-schedule.manage'),
    ('HumanResources', 'workforce.attendance-policy.manage'),
    ('HumanResources', 'workforce.attendance-classification.record'),
    ('HumanResources', 'workforce.attendance-classification.review'),
    ('Supervisor', 'workforce.attendance-schedule.manage'),
    ('Supervisor', 'workforce.attendance-classification.record'),
    ('Supervisor', 'workforce.attendance-classification.review'),
    ('ShopFloorTerminal', 'inventory.material-request'),
    ('Supervisor', 'inventory.material-request'),
    ('MaterialHandler', 'inventory.material-request'),
    ('Supervisor', 'inventory.custody.receive'),
    ('MaterialHandler', 'inventory.custody.receive'),
    ('InventoryController', 'inventory.custody.receive'),
    ('ShopFloorTerminal', 'quality.suspect.report'),
    ('Supervisor', 'quality.suspect.report'),
    ('Inspector', 'quality.suspect.report'),
    ('QualityEngineer', 'quality.suspect.report')
ON CONFLICT DO NOTHING;
