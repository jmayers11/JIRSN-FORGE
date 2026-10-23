INSERT INTO kernel.permission_catalog(permission)
VALUES
    ('workforce.development.nominate'),
    ('workforce.development.review'),
    ('workforce.development.read'),
    ('workforce.compensation.read')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('SecurityAdministrator', 'workforce.development.nominate'),
    ('SecurityAdministrator', 'workforce.development.review'),
    ('SecurityAdministrator', 'workforce.development.read'),
    ('SecurityAdministrator', 'workforce.compensation.read'),
    ('HumanResources', 'workforce.development.nominate'),
    ('HumanResources', 'workforce.development.review'),
    ('HumanResources', 'workforce.development.read'),
    ('HumanResources', 'workforce.compensation.read'),
    ('Supervisor', 'workforce.development.nominate'),
    ('Supervisor', 'workforce.development.review'),
    ('Supervisor', 'workforce.development.read'),
    ('TrainingCoordinator', 'workforce.development.nominate'),
    ('TrainingCoordinator', 'workforce.development.review'),
    ('TrainingCoordinator', 'workforce.development.read')
ON CONFLICT DO NOTHING;
