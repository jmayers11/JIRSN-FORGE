-- Optional human authority for customer communication proposals. No person gains the role by migration.
INSERT INTO kernel.permission_catalog(permission)
VALUES ('quality.communications.approve'),('quality.read'),('execution.orders.read'),
       ('procurement.read'),('engineering.read'),('inventory.read')
ON CONFLICT DO NOTHING;

WITH requested(role_name,display_name,permissions) AS (
    VALUES ('CustomerCommunicationsApprover','Customer Communications Approver',
        ARRAY['quality.communications.approve','quality.read','execution.orders.read',
              'procurement.read','engineering.read','inventory.read'])
), created AS (
    INSERT INTO kernel.role_definition(role_name,display_name)
    SELECT role_name,display_name FROM requested
    ON CONFLICT DO NOTHING
    RETURNING role_name
)
INSERT INTO kernel.role_permission(role_name,permission)
SELECT created.role_name,authority.permission
FROM created JOIN requested USING(role_name)
CROSS JOIN LATERAL unnest(requested.permissions || ARRAY['notifications.read','notifications.acknowledge','kernel.delegations.propose']) AS authority(permission)
ON CONFLICT DO NOTHING;
