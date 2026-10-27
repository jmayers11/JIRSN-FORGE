-- Owner decision 2026-10-27: supervisors and managers answer to several areas, so they see every area of Forge. This adds read access
-- only; recording, proposing and approving stay with each area's own role, so separation of duties is unchanged. A new Plant Manager
-- role sees the whole plant. People views (attendance intelligence, development packets, training requests) go to supervisors and the
-- plant manager only; pay, the audit trail and access administration are not widened.
INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('PlantManager', 'Plant Manager')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
SELECT role_name, permission
FROM unnest(ARRAY['Supervisor', 'PlantManager', 'QualityManager', 'PurchasingManager', 'ProgramManager', 'FacilitiesManager']) AS r(role_name)
CROSS JOIN unnest(ARRAY[
    'resources.read', 'workforce.read', 'competency.read', 'safety.rules.read', 'planning.readiness.read',
    'engineering.read', 'execution.orders.read', 'maintenance.read', 'planning.schedule.read', 'execution.shopfloor.read',
    'holds.read', 'inventory.read', 'procurement.read', 'quality.read', 'quality.qms.read',
    'identification.read', 'planning.mrp.read', 'programs.read', 'projects.read', 'costing.read',
    'planning.analyst.use', 'planning.reports.save']) AS p(permission)
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
SELECT role_name, permission
FROM unnest(ARRAY['Supervisor', 'PlantManager']) AS r(role_name)
CROSS JOIN unnest(ARRAY['workforce.attendance-intelligence.read', 'workforce.development.read', 'workforce.training-requests.read']) AS p(permission)
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('PlantManager', 'notifications.read'),
    ('PlantManager', 'notifications.acknowledge'),
    ('PlantManager', 'kernel.delegations.propose')
ON CONFLICT DO NOTHING;
