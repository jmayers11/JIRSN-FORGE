-- Roles created after the original common-authority migrations must receive the same
-- baseline notification and delegation grants as the rest of the role catalog.
INSERT INTO kernel.role_permission(role_name, permission)
SELECT 'HumanResources', permission
FROM (VALUES
    ('notifications.read'),
    ('notifications.acknowledge'),
    ('kernel.delegations.propose')
) AS baseline(permission)
ON CONFLICT DO NOTHING;

-- The source role catalog grants inventory posting to controllers; the database seed
-- predates that grant and needs to converge without rewriting the original migration.
INSERT INTO kernel.role_permission(role_name, permission)
VALUES ('InventoryController', 'inventory.transact')
ON CONFLICT DO NOTHING;
