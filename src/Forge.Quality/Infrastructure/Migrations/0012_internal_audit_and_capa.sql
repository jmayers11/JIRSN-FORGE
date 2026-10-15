-- Unknown cause/action is honestly null for newly governed cases; historic rows and CAPA IDs retain their values.
ALTER TABLE quality.capa ALTER COLUMN root_cause DROP NOT NULL;
ALTER TABLE quality.capa ALTER COLUMN action DROP NOT NULL;
CREATE TABLE quality.audit_head (
 entity_type text NOT NULL,entity_id text NOT NULL,version integer NOT NULL CHECK(version>0),
 owner_key text NOT NULL,PRIMARY KEY(entity_type,entity_id)
);
CREATE TABLE quality.audit_version (
 entity_type text NOT NULL,entity_id text NOT NULL,version integer NOT NULL CHECK(version>0),data jsonb NOT NULL,
 PRIMARY KEY(entity_type,entity_id,version),
 FOREIGN KEY(entity_type,entity_id) REFERENCES quality.audit_head(entity_type,entity_id)
);
CREATE INDEX audit_head_owner ON quality.audit_head(entity_type,owner_key,entity_id);
-- One governed extension per ORIGINAL core CAPA ID, with the core row remaining authoritative.
CREATE TABLE quality.capa_governance (
 capa_id bigint PRIMARY KEY REFERENCES quality.capa(capa_id),
 entity_type text NOT NULL DEFAULT 'Capa' CHECK(entity_type='Capa'),
 entity_id text NOT NULL UNIQUE,
 FOREIGN KEY(entity_type,entity_id) REFERENCES quality.audit_head(entity_type,entity_id),
 CHECK(entity_id=capa_id::text)
);
CREATE FUNCTION quality.reject_audit_evidence_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'internal audit/CAPA evidence versions are immutable (%)',TG_TABLE_NAME USING ERRCODE='insufficient_privilege';
END;
$$;
CREATE TRIGGER audit_version_immutable BEFORE UPDATE OR DELETE ON quality.audit_version
 FOR EACH ROW EXECUTE FUNCTION quality.reject_audit_evidence_change();
CREATE TRIGGER capa_governance_identity_immutable BEFORE UPDATE OR DELETE ON quality.capa_governance
 FOR EACH ROW EXECUTE FUNCTION quality.reject_audit_evidence_change();
CREATE FUNCTION quality.check_audit_head_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' OR (NEW.entity_type,NEW.entity_id,NEW.owner_key) IS DISTINCT FROM (OLD.entity_type,OLD.entity_id,OLD.owner_key) OR NEW.version<>OLD.version+1 THEN
  RAISE EXCEPTION 'audit/CAPA pointers retain identity and only advance one version' USING ERRCODE='insufficient_privilege';
 END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER audit_head_preserve BEFORE UPDATE OR DELETE ON quality.audit_head
 FOR EACH ROW EXECUTE FUNCTION quality.check_audit_head_change();
