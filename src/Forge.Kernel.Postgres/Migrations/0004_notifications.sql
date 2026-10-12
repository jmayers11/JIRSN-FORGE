CREATE TABLE kernel.notification_rule (
    rule_id uuid PRIMARY KEY,
    name text NOT NULL UNIQUE,
    active boolean NOT NULL DEFAULT true,
    trigger_event_type text NOT NULL,
    clear_event_type text NULL,
    scope_type text NOT NULL DEFAULT 'Global' CHECK (scope_type IN ('Global','Program','WorkCenter','Entity')),
    scope_id text NULL,
    condition_field text NULL,
    condition_equals text NULL,
    severity text NOT NULL CHECK (severity IN ('Information','Watch','ActionRequired','Critical')),
    confidence text NOT NULL CHECK (confidence IN ('Confirmed','High','Medium','Low','Unknown')),
    recipient_roles text[] NOT NULL CHECK (cardinality(recipient_roles) > 0),
    escalation_roles text[] NOT NULL DEFAULT '{}',
    channels text[] NOT NULL CHECK (cardinality(channels) > 0 AND channels <@ ARRAY['InApp','Email']::text[]),
    template text NOT NULL,
    resolution_template text NOT NULL,
    deep_link_template text NOT NULL DEFAULT '#/scheduling',
    delay_seconds integer NOT NULL DEFAULT 0 CHECK (delay_seconds BETWEEN 0 AND 259200),
    reminder_seconds integer NULL CHECK (reminder_seconds IS NULL OR reminder_seconds BETWEEN 60 AND 2592000),
    escalation_seconds integer NULL CHECK (escalation_seconds IS NULL OR escalation_seconds BETWEEN 60 AND 2592000),
    quiet_start time NULL,
    quiet_end time NULL,
    time_zone text NOT NULL DEFAULT 'UTC',
    dedup_seconds integer NOT NULL DEFAULT 86400 CHECK (dedup_seconds BETWEEN 0 AND 2592000),
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    updated_at timestamptz NOT NULL,
    updated_by text NOT NULL,
    CHECK ((scope_type IN ('Global') AND scope_id IS NULL) OR (scope_type <> 'Global' AND scope_id IS NOT NULL)),
    CHECK ((quiet_start IS NULL) = (quiet_end IS NULL))
);
CREATE INDEX notification_rule_event ON kernel.notification_rule(trigger_event_type) WHERE active;

CREATE TABLE kernel.notification_alert (
    alert_id uuid PRIMARY KEY,
    rule_id uuid NOT NULL REFERENCES kernel.notification_rule(rule_id),
    generation integer NOT NULL CHECK (generation > 0),
    condition_key text NOT NULL,
    state text NOT NULL CHECK (state IN ('Open','Resolved')),
    severity text NOT NULL CHECK (severity IN ('Information','Watch','ActionRequired','Critical')),
    confidence text NOT NULL CHECK (confidence IN ('Confirmed','High','Medium','Low','Unknown')),
    program_id uuid NULL,
    markings integer NOT NULL DEFAULT 0 CHECK (markings >= 0),
    source_context text NOT NULL,
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    source_event_id uuid NOT NULL,
    source_event_type text NOT NULL,
    title text NOT NULL,
    message text NOT NULL,
    deep_link text NOT NULL,
    opened_at timestamptz NOT NULL,
    visible_at timestamptz NOT NULL,
    resolved_at timestamptz NULL,
    resolution_message text NULL,
    UNIQUE(rule_id, condition_key, generation)
);
CREATE UNIQUE INDEX notification_alert_one_open ON kernel.notification_alert(rule_id, condition_key) WHERE state = 'Open';
CREATE INDEX notification_alert_program ON kernel.notification_alert(program_id, opened_at DESC);

CREATE TABLE kernel.notification_inbox (
    alert_id uuid NOT NULL REFERENCES kernel.notification_alert(alert_id),
    person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    recipient_role text NOT NULL,
    acknowledged_at timestamptz NULL,
    resolution_acknowledged_at timestamptz NULL,
    PRIMARY KEY(alert_id, person_id)
);
CREATE INDEX notification_inbox_person ON kernel.notification_inbox(person_id, acknowledged_at, alert_id);

CREATE TABLE kernel.notification_delivery (
    delivery_id uuid PRIMARY KEY,
    alert_id uuid NOT NULL REFERENCES kernel.notification_alert(alert_id),
    person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    recipient_role text NOT NULL,
    channel text NOT NULL CHECK (channel IN ('Email')),
    kind text NOT NULL CHECK (kind IN ('Alert','Resolution','Reminder','Escalation')),
    idempotency_key text NOT NULL UNIQUE,
    due_at timestamptz NOT NULL,
    sent_at timestamptz NULL,
    status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Processing','Sent','Failed','Suppressed')),
    attempts integer NOT NULL DEFAULT 0,
    last_error text NULL,
    next_attempt_at timestamptz NULL,
    lease_until timestamptz NULL,
    UNIQUE(alert_id, person_id, channel, kind, due_at)
);
CREATE INDEX notification_delivery_due ON kernel.notification_delivery(COALESCE(next_attempt_at, due_at), delivery_id) WHERE status IN ('Pending','Failed','Processing');

INSERT INTO kernel.permission_catalog(permission, description) VALUES
    ('notifications.read', 'Read current alerts addressed to your active roles and scopes'),
    ('notifications.acknowledge', 'Acknowledge an alert addressed to your active roles and scopes'),
    ('notifications.rules.manage', 'Configure notification and alert rules')
ON CONFLICT (permission) DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
SELECT r.role_name, p.permission
FROM kernel.role_definition r
CROSS JOIN (VALUES ('notifications.read'), ('notifications.acknowledge')) p(permission)
ON CONFLICT DO NOTHING;
INSERT INTO kernel.role_permission(role_name, permission)
SELECT role_name, 'notifications.rules.manage'
FROM kernel.role_definition
WHERE role_name IN ('SecurityAdministrator','Supervisor')
ON CONFLICT DO NOTHING;
