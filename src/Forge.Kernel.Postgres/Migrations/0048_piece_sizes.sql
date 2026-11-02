-- Owner decision 2026-11-02: the allocation optimizer offers the smallest leftover piece that fits a job. Engineers, planners and
-- inventory controllers set a job's minimum piece size; anyone who transacts stock can measure a lot.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES ('inventory.piece-sizes.manage', 'Set the smallest piece of a component a parent part''s job can use')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('ManufacturingEngineer', 'inventory.piece-sizes.manage'),
    ('Planner', 'inventory.piece-sizes.manage'),
    ('InventoryController', 'inventory.piece-sizes.manage')
ON CONFLICT DO NOTHING;
