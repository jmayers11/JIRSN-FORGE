-- Governed commercial evidence, never a customer/supplier/property or inventory master.
CREATE TABLE quality.commercial_revision (
 kind text NOT NULL CHECK(kind IN ('CustomerRequirements','DesignApplicability','ProviderEvaluation','PropertyAgreement','PropertyIncident','Complaint','CustomerFeedback','PostDelivery','Communication')),
 code text NOT NULL CHECK(length(code) BETWEEN 1 AND 40),
 revision integer NOT NULL CHECK(revision>0), version integer NOT NULL CHECK(version>0),
 state text NOT NULL CHECK(state IN ('Draft','Submitted','Reviewed','Approved','Effective','Superseded','Withdrawn','Closed')),
 organization_code text NOT NULL, facility_code text NOT NULL REFERENCES resources.site(code),
 effective_from timestamptz, effective_to timestamptz, payload jsonb NOT NULL,
 PRIMARY KEY(kind,code,revision), CHECK(effective_to IS NULL OR effective_from IS NOT NULL AND effective_to>=effective_from)
);
CREATE INDEX commercial_effective ON quality.commercial_revision(kind,state);
CREATE TABLE quality.commercial_version (
 kind text NOT NULL, code text NOT NULL, revision integer NOT NULL, version integer NOT NULL,
 payload jsonb NOT NULL, PRIMARY KEY(kind,code,revision,version),
 FOREIGN KEY(kind,code,revision) REFERENCES quality.commercial_revision(kind,code,revision)
);
CREATE FUNCTION quality.guard_commercial_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE i integer; field text;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Commercial evidence is retained'; END IF;
 IF TG_OP='UPDATE' THEN
  IF NEW.kind<>OLD.kind OR NEW.code<>OLD.code OR NEW.revision<>OLD.revision OR NEW.organization_code<>OLD.organization_code OR NEW.facility_code<>OLD.facility_code OR NEW.version<>OLD.version+1 THEN RAISE EXCEPTION 'Invalid commercial identity/version'; END IF;
  IF OLD.state<>'Draft' AND NEW.payload->'content' IS DISTINCT FROM OLD.payload->'content' THEN RAISE EXCEPTION 'Submitted commercial content is immutable'; END IF;
  IF OLD.state<>'Draft' THEN
   FOREACH field IN ARRAY ARRAY['ownership','sources','facts','sourceFingerprint'] LOOP
    IF NEW.payload->field IS DISTINCT FROM OLD.payload->field THEN RAISE EXCEPTION 'Captured commercial source evidence is immutable'; END IF;
   END LOOP;
  END IF;
  FOREACH field IN ARRAY ARRAY['observations','history'] LOOP
   IF jsonb_array_length(NEW.payload->field)<jsonb_array_length(OLD.payload->field) THEN RAISE EXCEPTION 'Commercial evidence cannot be removed'; END IF;
   FOR i IN 0..jsonb_array_length(OLD.payload->field)-1 LOOP
    IF NEW.payload->field->i IS DISTINCT FROM OLD.payload->field->i THEN RAISE EXCEPTION 'Recorded commercial evidence is immutable'; END IF;
   END LOOP;
  END LOOP;
  IF NEW.payload->'createdBy' IS DISTINCT FROM OLD.payload->'createdBy' OR NEW.payload->'createdAt' IS DISTINCT FROM OLD.payload->'createdAt' THEN RAISE EXCEPTION 'Commercial authorship is immutable'; END IF;
 END IF;
 IF NEW.payload->>'kind'<>NEW.kind OR NEW.payload->>'code'<>NEW.code OR (NEW.payload->>'revision')::integer<>NEW.revision OR (NEW.payload->>'version')::integer<>NEW.version OR NEW.payload->>'state'<>NEW.state THEN RAISE EXCEPTION 'Commercial snapshot mismatch'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER commercial_revision_guard BEFORE INSERT OR UPDATE OR DELETE ON quality.commercial_revision FOR EACH ROW EXECUTE FUNCTION quality.guard_commercial_revision();
CREATE FUNCTION quality.guard_commercial_version() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Commercial versions are immutable'; END $$;
CREATE TRIGGER commercial_version_guard BEFORE UPDATE OR DELETE ON quality.commercial_version FOR EACH ROW EXECUTE FUNCTION quality.guard_commercial_version();

CREATE TABLE quality.commercial_use (
 use_id uuid PRIMARY KEY, audit_id uuid NOT NULL UNIQUE REFERENCES kernel.audit_log(audit_id),
 context_fingerprint text NOT NULL CHECK(length(context_fingerprint)=64),
 component_fingerprint text NOT NULL CHECK(length(component_fingerprint)=64), payload jsonb NOT NULL
);
CREATE INDEX commercial_use_entity ON quality.commercial_use((payload->>'entityType'),(payload->>'entityId'),(payload#>>'{evidence,activity}'),(payload->>'at'));
CREATE FUNCTION quality.guard_commercial_use() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Accepted commercial use evidence is immutable'; END IF;
 IF NEW.payload->>'useId'<>NEW.use_id::text OR NEW.payload->>'auditId'<>NEW.audit_id::text OR NEW.payload->>'contextFingerprint'<>NEW.context_fingerprint OR NEW.payload->>'componentFingerprint'<>NEW.component_fingerprint THEN RAISE EXCEPTION 'Commercial usage identity mismatch'; END IF;
 IF NOT EXISTS(SELECT 1 FROM kernel.audit_log a WHERE a.audit_id=NEW.audit_id AND a.outcome='Succeeded' AND a.context_fingerprint=NEW.context_fingerprint) THEN RAISE EXCEPTION 'Commercial usage must bind the actual successful audit context'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER commercial_use_guard BEFORE INSERT OR UPDATE OR DELETE ON quality.commercial_use FOR EACH ROW EXECUTE FUNCTION quality.guard_commercial_use();
