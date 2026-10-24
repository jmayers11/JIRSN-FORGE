INSERT INTO kernel.permission_catalog(permission)
VALUES ('workforce.attendance-correction.request')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('Employee', 'Employee')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('Employee', 'workforce.attendance-correction.request'),
    ('SecurityAdministrator', 'workforce.attendance-correction.request'),
    ('HumanResources', 'workforce.attendance-correction.request'),
    ('Supervisor', 'workforce.attendance-correction.request')
ON CONFLICT DO NOTHING;
