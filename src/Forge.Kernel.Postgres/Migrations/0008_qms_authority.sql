-- V4 governed definitions: add canonical authority without granting existing users new roles.
INSERT INTO kernel.permission_catalog(permission) VALUES
    ('quality.qms.read'),('quality.qms.manage'),('quality.qms.review'),
    ('quality.qms.approve'),('quality.qms.publish'),('quality.product.release')
    ON CONFLICT DO NOTHING;
INSERT INTO kernel.role_definition(role_name,display_name) VALUES
    ('QmsAuthor','QMS Author'),('QmsReviewer','QMS Reviewer'),('QmsApprover','QMS Approver')
    ON CONFLICT DO NOTHING;
INSERT INTO kernel.role_permission(role_name,permission) VALUES
    ('QmsAuthor','quality.qms.read'),('QmsAuthor','quality.qms.manage'),('QmsAuthor','engineering.read'),
    ('QmsReviewer','quality.qms.read'),('QmsReviewer','quality.qms.review'),('QmsReviewer','engineering.read'),
    ('QmsApprover','quality.qms.read'),('QmsApprover','quality.qms.approve'),('QmsApprover','quality.qms.publish'),
    ('QmsApprover','engineering.read')
    ON CONFLICT DO NOTHING;
-- Product acceptance remains an explicitly assigned permission distinct from physical shipping.
