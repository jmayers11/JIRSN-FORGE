CREATE TABLE kernel.identification_scan_session (
    session_id uuid PRIMARY KEY,
    channel_hash text NOT NULL CHECK(length(channel_hash)=64),
    version integer NOT NULL CHECK(version>0),
    state text NOT NULL CHECK(state IN ('Active','Invalidated','Superseded')),
    principal_person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    operator_person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    source_type text NOT NULL CHECK(source_type='OrderOperation'),
    source_id text NOT NULL,
    facility_code text NOT NULL,
    program_id uuid NULL,
    markings integer NOT NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK(expires_at>created_at),
    payload jsonb NOT NULL
);
CREATE INDEX identification_scan_session_source_idx ON kernel.identification_scan_session(source_type,source_id);
CREATE TABLE kernel.identification_scan_channel (
    channel_hash text PRIMARY KEY CHECK(length(channel_hash)=64),
    session_id uuid NOT NULL REFERENCES kernel.identification_scan_session(session_id)
);
CREATE TABLE kernel.identification_scan_acceptance (
    acceptance_id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES kernel.identification_scan_session(session_id),
    correlation_id uuid NOT NULL,
    audit_id uuid NULL REFERENCES kernel.audit_log(audit_id),
    command_type text NOT NULL,
    entity_type text NULL,
    entity_id text NULL,
    operator_person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    recorded_at timestamptz NOT NULL,
    payload jsonb NOT NULL,
    UNIQUE(audit_id)
);
CREATE FUNCTION kernel.guard_scan_session() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Scan session history cannot be deleted'; END IF;
    IF ROW(NEW.session_id,NEW.channel_hash,NEW.principal_person_id,NEW.operator_person_id,NEW.source_type,NEW.source_id,
           NEW.facility_code,NEW.program_id,NEW.markings,NEW.created_at,NEW.expires_at)
       IS DISTINCT FROM ROW(OLD.session_id,OLD.channel_hash,OLD.principal_person_id,OLD.operator_person_id,OLD.source_type,OLD.source_id,
           OLD.facility_code,OLD.program_id,OLD.markings,OLD.created_at,OLD.expires_at)
       OR NEW.version<>OLD.version+1 OR OLD.state<>'Active'
       OR (NEW.payload - ARRAY['version','state','selections','invalidatedBy','invalidationReason','invalidatedAt','accepted'])
          IS DISTINCT FROM (OLD.payload - ARRAY['version','state','selections','invalidatedBy','invalidationReason','invalidatedAt','accepted'])
       OR NEW.payload->>'id'<>NEW.session_id::text OR (NEW.payload->>'version')::integer<>NEW.version
       OR NEW.payload->>'state'<>NEW.state THEN
        RAISE EXCEPTION 'Scan session identity and closed generations are immutable';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER guard_scan_session BEFORE UPDATE OR DELETE ON kernel.identification_scan_session
FOR EACH ROW EXECUTE FUNCTION kernel.guard_scan_session();
CREATE FUNCTION kernel.guard_scan_acceptance() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Accepted scan transaction provenance is immutable'; END $$;
CREATE TRIGGER guard_scan_acceptance BEFORE UPDATE OR DELETE ON kernel.identification_scan_acceptance
FOR EACH ROW EXECUTE FUNCTION kernel.guard_scan_acceptance();
