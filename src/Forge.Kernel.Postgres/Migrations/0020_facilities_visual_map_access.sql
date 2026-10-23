INSERT INTO kernel.permission_catalog(permission)
VALUES ('resources.floor-map.manage'), ('workforce.photos.manage')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('FacilitiesManager', 'resources.floor-map.manage'),
    ('FacilitiesManager', 'workforce.read'),
    ('FacilitiesManager', 'workforce.supervise'),
    ('FacilitiesManager', 'workforce.photos.manage'),
    ('ManufacturingEngineer', 'resources.floor-map.manage'),
    ('ManufacturingEngineer', 'workforce.photos.manage'),
    ('SecurityAdministrator', 'workforce.photos.manage'),
    ('SecurityAdministrator', 'resources.floor-map.manage')
ON CONFLICT DO NOTHING;
