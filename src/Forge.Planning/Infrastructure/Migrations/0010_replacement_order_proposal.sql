-- Sprint 10 (WW-S10-35): a proposed manufacturing order that replaces scrapped or lost production. It records the qualified-operator and
-- source-center check made when it was proposed; a different person approves (creating a planned order) or rejects it, once.
CREATE TABLE planning.replacement_order_proposal (
    proposal_id           uuid PRIMARY KEY,
    part_number           text NOT NULL,
    revision              text NOT NULL,
    quantity              numeric(18, 6) NOT NULL CHECK (quantity > 0),
    due_date              date NOT NULL,
    priority              int NOT NULL CHECK (priority BETWEEN 1 AND 9),
    ncr_id                bigint,
    replaces_order_number bigint,
    program_id            uuid,
    status                text NOT NULL CHECK (status IN ('Proposed', 'Approved', 'Rejected')),
    checks                jsonb NOT NULL,
    checked_at            timestamptz NOT NULL,
    rationale             text NOT NULL,
    proposed_by           text NOT NULL,
    proposed_by_person_id uuid,
    proposed_at           timestamptz NOT NULL,
    decided_by            text,
    decided_by_person_id  uuid,
    decided_at            timestamptz,
    decision_note         text,
    order_number          bigint,
    version               int NOT NULL,
    CHECK ((status = 'Proposed') = (decided_at IS NULL)),
    CHECK ((status = 'Approved') = (order_number IS NOT NULL))
);

CREATE INDEX replacement_order_proposal_open ON planning.replacement_order_proposal (proposed_at) WHERE status = 'Proposed';

-- Planners (who release orders) hear about a proposal once, with a link; it clears when someone decides it.
INSERT INTO kernel.notification_rule (rule_id, name, trigger_event_type, clear_event_type, severity, confidence, recipient_roles, escalation_roles, channels, template,
    resolution_template, deep_link_template, reminder_seconds, escalation_seconds, dedup_seconds, created_at, created_by, updated_at, updated_by)
VALUES ('0b1d5a57-0010-4a02-9a02-000000000035', 'Replacement order to decide', 'ReplacementOrderProposed', 'ReplacementOrderDecided', 'ActionRequired', 'Confirmed',
    ARRAY['Planner'], ARRAY['Planner'], ARRAY['InApp'], '{message}', 'The replacement order proposal was decided.', '#/orders', NULL, NULL, 86400,
    now(), 'system:migration', now(), 'system:migration')
ON CONFLICT DO NOTHING;
