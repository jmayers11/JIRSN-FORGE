-- Keep the already-applied 0008 bytes immutable; complete canonical role parity forward.
-- A pre-existing configured role with the same name remains organization-defined.
-- Restore only grants introduced by 0008 in that collision case; preserve all earlier grants/scopes.
DELETE FROM kernel.role_permission p
USING kernel.role_definition r, public.forge_schema_migrations m
WHERE m.migration_id='0008_qms_authority'
  AND r.role_name IN ('QmsAuthor','QmsReviewer','QmsApprover')
  AND p.role_name=r.role_name AND r.created_at<m.applied_at
  AND p.granted_by='system:migration' AND p.granted_at=m.applied_at;

INSERT INTO kernel.role_permission(role_name,permission)
SELECT r.role_name,p.permission
FROM kernel.role_definition r
JOIN public.forge_schema_migrations m ON m.migration_id='0008_qms_authority' AND r.created_at=m.applied_at
CROSS JOIN (VALUES ('notifications.read'),('notifications.acknowledge'),('kernel.delegations.propose')) p(permission)
WHERE r.role_name IN ('QmsAuthor','QmsReviewer','QmsApprover')
ON CONFLICT DO NOTHING;
