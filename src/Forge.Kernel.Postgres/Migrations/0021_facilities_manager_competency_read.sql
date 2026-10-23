-- Workforce supervisors must be able to verify current qualifications before proposing specialist placements.
INSERT INTO kernel.role_permission(role_name, permission)
VALUES ('FacilitiesManager', 'competency.read')
ON CONFLICT DO NOTHING;
