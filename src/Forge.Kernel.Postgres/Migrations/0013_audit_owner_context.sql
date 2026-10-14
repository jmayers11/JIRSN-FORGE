-- Existing row values and hashes are untouched. Only future writes supply optional hashed ownership context.
ALTER TABLE kernel.audit_log ADD COLUMN owning_resource jsonb NULL
    CHECK(owning_resource IS NULL OR jsonb_typeof(owning_resource)='object');
ALTER TABLE kernel.audit_log ADD COLUMN context_fingerprint text NULL
    CHECK(context_fingerprint IS NULL OR context_fingerprint ~ '^[0-9a-f]{64}$');

-- Accepted scan evidence must bind the actual succeeded audit, operator and outbox events.
-- Its exact context fingerprint is covered by the existing audit hash chain.
CREATE FUNCTION kernel.validate_scan_acceptance() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE audit_row kernel.audit_log%ROWTYPE; session_row kernel.identification_scan_session%ROWTYPE;
BEGIN
    SELECT * INTO STRICT audit_row FROM kernel.audit_log WHERE audit_id=NEW.audit_id;
    SELECT * INTO STRICT session_row FROM kernel.identification_scan_session WHERE session_id=NEW.session_id;
    IF audit_row.outcome<>'Succeeded' OR audit_row.action<>NEW.command_type
        OR audit_row.correlation_id<>NEW.correlation_id
        OR audit_row.entity_type IS DISTINCT FROM NEW.entity_type OR audit_row.entity_id IS DISTINCT FROM NEW.entity_id
        OR audit_row.context_fingerprint IS NULL
        OR audit_row.context_fingerprint IS DISTINCT FROM NEW.payload->>'evidenceFingerprint'
        OR audit_row.actor IS DISTINCT FROM NEW.payload->>'actor'
        OR session_row.operator_person_id<>NEW.operator_person_id
        OR NEW.payload->>'acceptanceId' IS DISTINCT FROM NEW.acceptance_id::text
        OR NEW.payload->>'auditId' IS DISTINCT FROM NEW.audit_id::text
        OR NEW.payload->>'correlationId' IS DISTINCT FROM NEW.correlation_id::text
        OR NEW.payload->>'operatorPersonId' IS DISTINCT FROM NEW.operator_person_id::text
        OR NEW.payload->>'commandType' IS DISTINCT FROM NEW.command_type
        OR NEW.payload->>'entityType' IS DISTINCT FROM NEW.entity_type
        OR NEW.payload->>'entityId' IS DISTINCT FROM NEW.entity_id
        OR EXISTS (SELECT 1 FROM jsonb_array_elements_text(NEW.payload->'eventIds') AS required_event(event_id_text)
            WHERE NOT EXISTS(SELECT 1 FROM kernel.outbox event_row WHERE event_row.event_id=required_event.event_id_text::uuid)) THEN
        RAISE EXCEPTION 'Accepted scan evidence must match its actual succeeded audit and events';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER validate_scan_acceptance BEFORE INSERT ON kernel.identification_scan_acceptance
FOR EACH ROW EXECUTE FUNCTION kernel.validate_scan_acceptance();
