-- ADR 0057 §2.4: planners are told when a failed inspection or a new NCR moves orders' projected completions.
-- Administrators can change or switch off this rule like any other.
INSERT INTO kernel.notification_rule (rule_id, name, trigger_event_type, clear_event_type, severity, confidence, recipient_roles, channels, template,
    resolution_template, deep_link_template, dedup_seconds, created_at, created_by, updated_at, updated_by)
VALUES ('0b1d5a57-0057-4a02-9a02-000000000001', 'Schedule impact after a quality event', 'ScheduleImpactFound', 'ScheduleChanged', 'ActionRequired', 'Confirmed',
    ARRAY['Planner'], ARRAY['InApp'], '{message}', 'A schedule was approved.', '#/scheduling', 0, now(), 'system:migration', now(), 'system:migration')
ON CONFLICT DO NOTHING;
