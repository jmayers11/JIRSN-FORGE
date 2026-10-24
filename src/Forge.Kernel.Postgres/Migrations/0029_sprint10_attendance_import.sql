INSERT INTO kernel.permission_catalog(permission)
VALUES ('workforce.attendance.import')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('AttendanceImporter', 'Attendance Importer')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('SecurityAdministrator', 'workforce.attendance.import'),
    ('HumanResources', 'workforce.attendance.import'),
    ('AttendanceImporter', 'workforce.attendance.import')
ON CONFLICT DO NOTHING;
