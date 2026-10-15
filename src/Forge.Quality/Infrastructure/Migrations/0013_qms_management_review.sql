-- Review heads advance by compare-and-swap; source versions and captured inputs are immutable.
CREATE TABLE quality.review_head (
 entity_type text NOT NULL, entity_id text NOT NULL, version integer NOT NULL CHECK(version>0),
 code text NOT NULL, kind text NOT NULL, PRIMARY KEY(entity_type,entity_id)
);
CREATE TABLE quality.review_version (
 entity_type text NOT NULL, entity_id text NOT NULL, version integer NOT NULL CHECK(version>0),
 data jsonb NOT NULL, PRIMARY KEY(entity_type,entity_id,version),
 FOREIGN KEY(entity_type,entity_id) REFERENCES quality.review_head(entity_type,entity_id)
);
CREATE INDEX review_head_children ON quality.review_head(entity_type,code,entity_id);
CREATE TABLE quality.review_input_manifest (
 manifest_id uuid PRIMARY KEY, review_id uuid NOT NULL UNIQUE, fingerprint text NOT NULL,
 data_cutoff timestamptz NOT NULL, data jsonb NOT NULL
);
CREATE FUNCTION quality.reject_review_evidence_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'review versions and frozen input manifests are immutable (%)', TG_TABLE_NAME USING ERRCODE='insufficient_privilege';
END;
$$;
CREATE TRIGGER review_version_immutable BEFORE UPDATE OR DELETE ON quality.review_version
 FOR EACH ROW EXECUTE FUNCTION quality.reject_review_evidence_change();
CREATE TRIGGER review_manifest_immutable BEFORE UPDATE OR DELETE ON quality.review_input_manifest
 FOR EACH ROW EXECUTE FUNCTION quality.reject_review_evidence_change();
CREATE FUNCTION quality.check_review_head_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' OR (NEW.entity_type,NEW.entity_id,NEW.code,NEW.kind) IS DISTINCT FROM (OLD.entity_type,OLD.entity_id,OLD.code,OLD.kind)
   OR NEW.version<>OLD.version+1 THEN
  RAISE EXCEPTION 'review identity pointers only advance by one version' USING ERRCODE='insufficient_privilege';
 END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER review_head_preserve BEFORE UPDATE OR DELETE ON quality.review_head
 FOR EACH ROW EXECUTE FUNCTION quality.check_review_head_change();
