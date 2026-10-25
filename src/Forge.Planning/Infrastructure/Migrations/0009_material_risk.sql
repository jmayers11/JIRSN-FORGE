-- Sprint 10 (WW-S10-30): released orders at risk of missing their due date because of material. Only the current set is kept, so a
-- scan raises an event only when an order becomes at risk or stops being at risk; history is in the events and the audit trail.
CREATE TABLE planning.material_risk (
    order_number   bigint PRIMARY KEY,
    kind           text NOT NULL CHECK (kind IN ('Shortfall', 'OverdueSupply', 'LateSupply')),
    message        text NOT NULL,
    detected_at    timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL
);

-- Planners hear about it once per order (deduplicated), with a link to the order; each recipient's access to the order is rechecked
-- before an alert or email is shown. In-app by default; administrators add Email once the verified sender is configured.
INSERT INTO kernel.notification_rule (rule_id, name, trigger_event_type, clear_event_type, severity, confidence, recipient_roles, escalation_roles, channels, template,
    resolution_template, deep_link_template, reminder_seconds, escalation_seconds, dedup_seconds, created_at, created_by, updated_at, updated_by)
VALUES ('0b1d5a57-0010-4a02-9a02-000000000030', 'Order at risk from material', 'MaterialRiskDetected', 'MaterialRiskCleared', 'ActionRequired', 'Confirmed',
    ARRAY['Planner'], ARRAY['Planner'], ARRAY['InApp'], '{message}', 'The order''s material is covered in time again, or the order is no longer released.', '#/orders/{entityId}', NULL, NULL, 86400,
    now(), 'system:migration', now(), 'system:migration')
ON CONFLICT DO NOTHING;
