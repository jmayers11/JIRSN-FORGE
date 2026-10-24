-- Planners staff work, plan coverage and read skill-capacity forecasts, all of which rest on qualifications and machine
-- authorizations. Reading them (never changing them) is part of planning; certification and authorization stay with Competency roles.
INSERT INTO kernel.role_permission(role_name, permission)
VALUES ('Planner', 'competency.read')
ON CONFLICT DO NOTHING;
