-- Governance only. No owner record, audit, outbox, event or Engineering file is deleted.
CREATE TABLE quality.planning_head (
 entity_type text NOT NULL, entity_id text NOT NULL, version integer NOT NULL CHECK(version>0),
 code text, kind text, PRIMARY KEY(entity_type,entity_id)
);
CREATE TABLE quality.planning_version (
 entity_type text NOT NULL, entity_id text NOT NULL, version integer NOT NULL CHECK(version>0),
 data jsonb NOT NULL, PRIMARY KEY(entity_type,entity_id,version),
 FOREIGN KEY(entity_type,entity_id) REFERENCES quality.planning_head(entity_type,entity_id)
);
CREATE INDEX planning_head_code ON quality.planning_head(entity_type,code,entity_id);
CREATE TABLE quality.disposition_manifest (
 disposition_id uuid NOT NULL, manifest_id uuid PRIMARY KEY, version integer NOT NULL CHECK(version>0),
 fingerprint text NOT NULL, data jsonb NOT NULL, UNIQUE(disposition_id,version)
);
CREATE FUNCTION quality.reject_planning_evidence_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'planning and disposition evidence versions are immutable (%)', TG_TABLE_NAME USING ERRCODE='insufficient_privilege';
END;
$$;
CREATE TRIGGER planning_version_immutable BEFORE UPDATE OR DELETE ON quality.planning_version
 FOR EACH ROW EXECUTE FUNCTION quality.reject_planning_evidence_change();
CREATE TRIGGER disposition_manifest_immutable BEFORE UPDATE OR DELETE ON quality.disposition_manifest
 FOR EACH ROW EXECUTE FUNCTION quality.reject_planning_evidence_change();
CREATE FUNCTION quality.check_planning_head_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' OR (NEW.entity_type,NEW.entity_id,NEW.code,NEW.kind) IS DISTINCT FROM (OLD.entity_type,OLD.entity_id,OLD.code,OLD.kind) OR NEW.version<>OLD.version+1 THEN
  RAISE EXCEPTION 'planning identity/scope pointers only advance by one version' USING ERRCODE='insufficient_privilege';
 END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER planning_head_preserve BEFORE UPDATE OR DELETE ON quality.planning_head
 FOR EACH ROW EXECUTE FUNCTION quality.check_planning_head_change();
