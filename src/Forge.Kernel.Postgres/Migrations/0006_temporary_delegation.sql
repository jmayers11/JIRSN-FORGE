INSERT INTO kernel.permission_catalog(permission) VALUES ('kernel.delegations.propose') ON CONFLICT DO NOTHING;
INSERT INTO kernel.role_permission(role_name,permission)
SELECT role_name,'kernel.delegations.propose' FROM kernel.role_definition ON CONFLICT DO NOTHING;

CREATE TABLE kernel.authority_delegation (
    delegation_id uuid PRIMARY KEY,
    grantor_id uuid NOT NULL REFERENCES kernel.person(person_id),
    recipient_id uuid NOT NULL REFERENCES kernel.person(person_id),
    permissions text[] NOT NULL CHECK (cardinality(permissions) BETWEEN 1 AND 50),
    scopes jsonb NOT NULL CHECK (jsonb_typeof(scopes) = 'array'),
    starts_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > starts_at),
    status text NOT NULL CHECK (status IN ('PendingApproval','Approved','Revoked')),
    proposed_at timestamptz NOT NULL,
    reason text NOT NULL CHECK (length(btrim(reason)) > 0),
    approver_id uuid REFERENCES kernel.person(person_id),
    approved_at timestamptz,
    approval_reason text,
    revoked_at timestamptz,
    revocation_reason text,
    CHECK (grantor_id <> recipient_id),
    CHECK (approver_id IS NULL OR (approver_id <> grantor_id AND approver_id <> recipient_id)),
    CHECK (status <> 'Approved' OR (approver_id IS NOT NULL AND approved_at IS NOT NULL AND approval_reason IS NOT NULL AND length(btrim(approval_reason)) > 0)),
    CHECK (status <> 'Revoked' OR (revoked_at IS NOT NULL AND revocation_reason IS NOT NULL AND length(btrim(revocation_reason)) > 0))
);
CREATE INDEX authority_delegation_recipient_interval ON kernel.authority_delegation(recipient_id,starts_at,expires_at) WHERE status='Approved';
