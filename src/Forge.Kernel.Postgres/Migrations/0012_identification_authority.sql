-- New optional roles grant no existing person additional authority, and never modify a configured same-name role.
INSERT INTO kernel.permission_catalog(permission) VALUES
    ('identification.read'),('identification.labels.manage'),('identification.labels.approve'),
    ('identification.labels.print'),('identification.aliases.manage'),('identification.scan.stage')
    ON CONFLICT DO NOTHING;
WITH requested(role_name,display_name,permissions) AS (
    VALUES
      ('QmsPolicyConsumer','QMS Policy Consumer',ARRAY['quality.qms.read','engineering.read']),
      ('LabelAuthor','Label Author',ARRAY['identification.read','identification.labels.manage']),
      ('LabelApprover','Label Approver',ARRAY['identification.read','identification.labels.approve']),
      ('LabelPrinter','Label Printer',ARRAY['identification.read','identification.labels.print']),
      ('IdentificationAdministrator','Identification Administrator',ARRAY['identification.read','identification.aliases.manage']),
      ('ScanOperator','Scan Operator',ARRAY['identification.read','identification.scan.stage'])
), created AS (
    INSERT INTO kernel.role_definition(role_name,display_name)
    SELECT role_name,display_name FROM requested
    ON CONFLICT DO NOTHING RETURNING role_name
)
INSERT INTO kernel.role_permission(role_name,permission)
SELECT created.role_name,authority.permission FROM created JOIN requested USING(role_name)
CROSS JOIN LATERAL unnest(requested.permissions || ARRAY['notifications.read','notifications.acknowledge','kernel.delegations.propose']) AS authority(permission)
ON CONFLICT DO NOTHING;
