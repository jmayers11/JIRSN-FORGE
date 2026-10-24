-- Every role except the narrow integration identities can read and acknowledge its notifications and propose a delegation
-- (Roles static map). The Employee role added for attendance corrections (0030) was seeded without them.
INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('Employee', 'notifications.read'),
    ('Employee', 'notifications.acknowledge'),
    ('Employee', 'kernel.delegations.propose')
ON CONFLICT DO NOTHING;
