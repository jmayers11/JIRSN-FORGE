-- Kernel foundation: people and access, event outbox and log, audit chain, idempotency.
CREATE SCHEMA IF NOT EXISTS kernel;

CREATE TABLE kernel.person (
    person_id      uuid PRIMARY KEY,
    idp_subject    text NOT NULL UNIQUE,
    display_name   text NOT NULL,
    is_active      boolean NOT NULL DEFAULT true,
    markings       integer NOT NULL DEFAULT 0 CHECK (markings >= 0),
    created_at     timestamptz NOT NULL,
    created_by     text NOT NULL,
    version        integer NOT NULL DEFAULT 1
);

CREATE TABLE kernel.role_assignment (
    person_id   uuid NOT NULL REFERENCES kernel.person(person_id),
    role        text NOT NULL,
    granted_at  timestamptz NOT NULL,
    granted_by  text NOT NULL,
    PRIMARY KEY (person_id, role)
);

CREATE TABLE kernel.program_access (
    person_id   uuid NOT NULL REFERENCES kernel.person(person_id),
    program_id  uuid NOT NULL,
    granted_at  timestamptz NOT NULL,
    granted_by  text NOT NULL,
    PRIMARY KEY (person_id, program_id)
);

-- Written in the same transaction as the state change; relayed to event_log afterwards.
CREATE TABLE kernel.outbox (
    position      bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    event_id      uuid PRIMARY KEY,
    envelope      text NOT NULL,
    enqueued_at   timestamptz NOT NULL,
    relayed_at    timestamptz NULL,
    attempts      integer NOT NULL DEFAULT 0,
    last_error    text NULL
);
CREATE INDEX outbox_pending ON kernel.outbox (position) WHERE relayed_at IS NULL;

-- Append-only history of every event, in relay order.
CREATE TABLE kernel.event_log (
    sequence        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id        uuid NOT NULL UNIQUE,
    event_type      text NOT NULL,
    schema_version  integer NOT NULL,
    occurred_at     timestamptz NOT NULL,
    recorded_at     timestamptz NOT NULL,
    entity_type     text NOT NULL,
    entity_id       text NOT NULL,
    correlation_id  uuid NOT NULL,
    causation_id    uuid NULL,
    program_id      uuid NULL,
    envelope        text NOT NULL
);
CREATE INDEX event_log_entity ON kernel.event_log (entity_type, entity_id, sequence);
CREATE INDEX event_log_correlation ON kernel.event_log (correlation_id);

-- Per-subscriber position in the event log (at-least-once, in order).
CREATE TABLE kernel.subscriber_checkpoint (
    subscriber     text PRIMARY KEY,
    last_sequence  bigint NOT NULL
);

-- Tamper-evident business audit.
CREATE TABLE kernel.audit_log (
    sequence        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    audit_id        uuid NOT NULL UNIQUE,
    recorded_at     timestamptz NOT NULL,
    actor           text NOT NULL,
    on_behalf_of    text NULL,
    action          text NOT NULL,
    outcome         text NOT NULL CHECK (outcome IN ('Succeeded', 'Denied', 'Rejected')),
    entity_type     text NOT NULL,
    entity_id       text NOT NULL,
    old_value       text NULL,
    new_value       text NULL,
    reason          text NULL,
    source          text NOT NULL,
    correlation_id  uuid NOT NULL,
    program_id      uuid NULL,
    markings        integer NOT NULL DEFAULT 0,
    previous_hash   text NULL,
    hash            text NOT NULL UNIQUE
);
CREATE INDEX audit_log_entity ON kernel.audit_log (entity_type, entity_id, sequence);

CREATE TABLE kernel.idempotency (
    idempotency_key  text PRIMARY KEY,
    command_type     text NOT NULL,
    request_hash     text NOT NULL,
    created_at       timestamptz NOT NULL,
    result           text NULL
);

-- Append-only guards: history tables reject UPDATE and DELETE regardless of database role.
CREATE FUNCTION kernel.reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'kernel.% is append-only', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER audit_log_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON kernel.audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION kernel.reject_modification();
CREATE TRIGGER event_log_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON kernel.event_log
    FOR EACH STATEMENT EXECUTE FUNCTION kernel.reject_modification();
