-- Presentation metadata only. No stock, production quantity, credentials, or parallel entity catalog.
CREATE TABLE kernel.identification_presentation (
    kind text NOT NULL CHECK(kind IN ('Template','Issuance','Copy','PrintAttempt','Alias')),
    key text NOT NULL CHECK(length(key) BETWEEN 1 AND 100),
    namespace text NOT NULL CHECK(namespace ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
    subject_code text NULL CHECK(subject_code IS NULL OR length(subject_code) BETWEEN 1 AND 1024),
    version integer NOT NULL CHECK(version > 0),
    state text NOT NULL CHECK(length(state) BETWEEN 1 AND 40),
    alias_domain text NULL CHECK(alias_domain IS NULL OR length(alias_domain) BETWEEN 1 AND 64),
    alias_value text NULL CHECK(alias_value IS NULL OR length(alias_value) BETWEEN 1 AND 1024),
    PRIMARY KEY(kind,key),
    CHECK((kind='Alias')=(alias_domain IS NOT NULL AND alias_value IS NOT NULL))
);
CREATE TABLE kernel.identification_presentation_version (
    kind text NOT NULL,
    key text NOT NULL,
    version integer NOT NULL CHECK(version > 0),
    record jsonb NOT NULL CHECK(jsonb_typeof(record)='object'),
    transition jsonb NOT NULL CHECK(jsonb_typeof(transition)='object'),
    PRIMARY KEY(kind,key,version),
    FOREIGN KEY(kind,key) REFERENCES kernel.identification_presentation(kind,key)
);
CREATE INDEX presentation_subject ON kernel.identification_presentation(kind,subject_code,key);
CREATE INDEX presentation_artifact ON kernel.identification_presentation_version((record->>'artifactSha256')) WHERE kind='PrintAttempt';
ALTER TABLE kernel.identification_presentation ADD CONSTRAINT presentation_current_version
    FOREIGN KEY(kind,key,version) REFERENCES kernel.identification_presentation_version(kind,key,version) DEFERRABLE INITIALLY DEFERRED;
CREATE UNIQUE INDEX alias_active_value ON kernel.identification_presentation(namespace,alias_domain,alias_value)
    WHERE kind='Alias' AND state='Active';
CREATE FUNCTION kernel.guard_presentation_head() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Presentation history is never deleted'; END IF;
    IF NEW.kind<>OLD.kind OR NEW.key<>OLD.key OR NEW.namespace<>OLD.namespace
        OR NEW.subject_code IS DISTINCT FROM OLD.subject_code OR NEW.alias_domain IS DISTINCT FROM OLD.alias_domain
        OR NEW.alias_value IS DISTINCT FROM OLD.alias_value OR NEW.version<>OLD.version+1 THEN
        RAISE EXCEPTION 'Presentation identity is immutable and version must advance exactly once';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER presentation_head_guard BEFORE UPDATE OR DELETE ON kernel.identification_presentation
    FOR EACH ROW EXECUTE FUNCTION kernel.guard_presentation_head();
CREATE FUNCTION kernel.reject_presentation_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Presentation versions are append-only'; END $$;
CREATE TRIGGER presentation_history_immutable BEFORE UPDATE OR DELETE ON kernel.identification_presentation_version
    FOR EACH ROW EXECUTE FUNCTION kernel.reject_presentation_history_change();
