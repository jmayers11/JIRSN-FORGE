INSERT INTO kernel.permission_catalog(permission)
VALUES ('competency.machine.authorize')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('SecurityAdministrator', 'competency.machine.authorize'),
    ('Supervisor', 'competency.machine.authorize'),
    ('TrainingCoordinator', 'competency.machine.authorize'),
    ('Certifier', 'competency.machine.authorize')
ON CONFLICT DO NOTHING;
