INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('SecurityAdministrator', 'resources.manage'),
    ('SecurityAdministrator', 'resources.read'),
    ('SecurityAdministrator', 'workforce.manage'),
    ('SecurityAdministrator', 'workforce.read')
ON CONFLICT DO NOTHING;
