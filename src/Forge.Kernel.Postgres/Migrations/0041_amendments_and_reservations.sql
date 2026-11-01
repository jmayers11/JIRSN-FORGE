-- Owner decisions 2026-10-31: manufacturing engineers propose released-order amendments and planners approve them; releasing an order
-- or approving a sales order reserves stock, and inventory controllers keep reservations.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('execution.orders.amend.propose', 'Propose an amendment to a released production order'),
    ('execution.orders.amend.approve', 'Approve, and so apply, an amendment someone else proposed'),
    ('inventory.reserve', 'Reserve stock for released orders and approved sales orders')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('ManufacturingEngineer', 'execution.orders.amend.propose'),
    ('Planner', 'execution.orders.amend.approve'),
    ('InventoryController', 'inventory.reserve')
ON CONFLICT DO NOTHING;
