-- Scan failures are separate, minimized telemetry. Raw codes, lots, locations and credentials are not columns here.
-- The owner store enforces both configured age retention and the per-session row cap on every insert.
CREATE TABLE kernel.identification_scan_attempt (
    attempt_id uuid PRIMARY KEY,
    attempt_sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE NOT NULL,
    session_id uuid NOT NULL REFERENCES kernel.identification_scan_session(session_id),
    session_version integer NOT NULL CHECK (session_version > 0),
    command_type text NOT NULL CHECK (length(command_type) BETWEEN 1 AND 100),
    outcome text NOT NULL CHECK (outcome IN ('Rejected','Denied')),
    validation_code text NOT NULL CHECK (length(validation_code) BETWEEN 1 AND 100),
    recorded_at timestamptz NOT NULL,
    correlation_id uuid NOT NULL,
    audit_id uuid NOT NULL UNIQUE REFERENCES kernel.audit_log(audit_id),
    operator_person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    authenticated_actor text NOT NULL CHECK (length(authenticated_actor) BETWEEN 1 AND 200),
    assurance_summary text NULL CHECK (assurance_summary IS NULL OR length(assurance_summary) <= 40),
    workstation_binding_hash text NULL CHECK (workstation_binding_hash IS NULL OR workstation_binding_hash ~ '^[0-9a-f]{64}$'),
    entity_type text NULL CHECK (entity_type IS NULL OR length(entity_type) <= 100),
    entity_id text NULL CHECK (entity_id IS NULL OR length(entity_id) <= 256)
);
CREATE INDEX identification_scan_attempt_session_time_idx
    ON kernel.identification_scan_attempt(session_id, recorded_at DESC, attempt_sequence DESC);

CREATE FUNCTION kernel.validate_scan_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE audit_row kernel.audit_log%ROWTYPE; session_row kernel.identification_scan_session%ROWTYPE;
BEGIN
    SELECT * INTO STRICT audit_row FROM kernel.audit_log WHERE audit_id=NEW.audit_id;
    SELECT * INTO STRICT session_row FROM kernel.identification_scan_session WHERE session_id=NEW.session_id;
    IF audit_row.outcome IS DISTINCT FROM NEW.outcome
        OR audit_row.action IS DISTINCT FROM NEW.command_type
        OR audit_row.recorded_at IS DISTINCT FROM NEW.recorded_at
        OR audit_row.correlation_id IS DISTINCT FROM NEW.correlation_id
        OR audit_row.actor IS DISTINCT FROM NEW.authenticated_actor
        OR audit_row.entity_type IS DISTINCT FROM NEW.entity_type
        OR audit_row.entity_id IS DISTINCT FROM NEW.entity_id
        OR session_row.operator_person_id IS DISTINCT FROM NEW.operator_person_id THEN
        RAISE EXCEPTION 'Scan attempt metadata must match its actual audit and server session';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER validate_scan_attempt BEFORE INSERT ON kernel.identification_scan_attempt
FOR EACH ROW EXECUTE FUNCTION kernel.validate_scan_attempt();
