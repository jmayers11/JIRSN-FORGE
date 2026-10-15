-- Explicit optional roles, unassigned by migration. Configured same-name roles are untouched.
INSERT INTO kernel.permission_catalog(permission) VALUES
 ('quality.audit.manage'),('quality.audit.conduct'),('quality.audit.approve') ON CONFLICT DO NOTHING;
WITH requested(role_name,display_name,permissions) AS (
 VALUES
 ('AuditPlanner','Internal Audit Planner',ARRAY['quality.audit.manage','quality.read','quality.qms.read','engineering.read','competency.read']),
 ('InternalAuditor','Internal Auditor',ARRAY['quality.audit.conduct','quality.read','quality.qms.read','engineering.read','competency.read']),
 ('AuditApprover','Internal Audit Approver',ARRAY['quality.audit.approve','quality.read','quality.qms.read','engineering.read','competency.read'])
), created AS (
 INSERT INTO kernel.role_definition(role_name,display_name)
 SELECT role_name,display_name FROM requested ON CONFLICT DO NOTHING RETURNING role_name
)
INSERT INTO kernel.role_permission(role_name,permission)
SELECT created.role_name,authority.permission FROM created JOIN requested USING(role_name)
CROSS JOIN LATERAL unnest(requested.permissions||ARRAY['notifications.read','notifications.acknowledge','kernel.delegations.propose']) AS authority(permission)
ON CONFLICT DO NOTHING;
