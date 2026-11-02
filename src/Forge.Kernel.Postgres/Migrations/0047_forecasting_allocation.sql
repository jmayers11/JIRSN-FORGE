-- Owner decisions 2026-11-02: demand forecasting (planners run and approve forecasts) and the V5 allocation optimizer (planners and
-- inventory controllers approve proposals into reservations).
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('planning.forecast.manage', 'Generate, import and clear demand history, set forecast rules and run forecasts'),
    ('planning.allocation.approve', 'Approve allocation proposals into reservations and set the ranking rules')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('Planner', 'planning.forecast.manage'),
    ('Planner', 'planning.allocation.approve'),
    ('InventoryController', 'planning.allocation.approve')
ON CONFLICT DO NOTHING;
