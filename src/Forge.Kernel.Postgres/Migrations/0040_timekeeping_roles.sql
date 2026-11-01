-- Owner decision 2026-10-31: DCAA-style timesheets. Employees record and sign their own time, supervisors approve other people's,
-- cost accountants keep the charge codes; HR, auditors, the plant manager and cost accountants read timesheets.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('timekeeping.record', 'Record, correct and sign one''s own timesheet'),
    ('timekeeping.approve', 'Approve or return other people''s signed timesheets'),
    ('timekeeping.read', 'Read everyone''s timesheets'),
    ('timekeeping.codes.manage', 'Keep the charge codes time is recorded against')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('Employee', 'timekeeping.record'),
    ('Supervisor', 'timekeeping.approve'),
    ('Supervisor', 'timekeeping.read'),
    ('CostAccountant', 'timekeeping.read'),
    ('CostAccountant', 'timekeeping.codes.manage'),
    ('HumanResources', 'timekeeping.read'),
    ('Auditor', 'timekeeping.read'),
    ('PlantManager', 'timekeeping.read')
ON CONFLICT DO NOTHING;
