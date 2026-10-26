-- Owner decision 2026-10-25 (Sprint 10 WW-S10-24): costs follow US GAAP practice. Standard cost with full absorption (material, labor and
-- overhead) is the default method; a Cost Accountant keeps the costing policy, standard costs and rates. Planners, inventory controllers
-- and quality managers read costs where their decisions use them.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('costing.read', 'Read the costing policy, standard costs and cost reconciliations'),
    ('costing.manage', 'Set the costing policy, standard costs and labor and overhead rates')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('CostAccountant', 'Cost Accountant')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('CostAccountant', 'costing.read'),
    ('CostAccountant', 'costing.manage'),
    ('CostAccountant', 'engineering.read'),
    ('CostAccountant', 'inventory.read'),
    ('CostAccountant', 'execution.orders.read'),
    ('CostAccountant', 'quality.read'),
    ('CostAccountant', 'resources.read'),
    ('CostAccountant', 'notifications.read'),
    ('CostAccountant', 'notifications.acknowledge'),
    ('CostAccountant', 'kernel.delegations.propose'),
    ('Planner', 'costing.read'),
    ('InventoryController', 'costing.read'),
    ('QualityManager', 'costing.read')
ON CONFLICT DO NOTHING;
