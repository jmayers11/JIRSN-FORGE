-- Sprint 10 §16.2: Quality's reviewed cause of a first-pass inspection result. The inspection is never changed; official process
-- FPY keeps every inspected unit, and only verified attribution feeds the separate employee-attributable diagnostic.
CREATE TABLE quality.first_pass_attribution (
    attribution_id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    inspection_id              bigint NOT NULL REFERENCES quality.inspection(inspection_id),
    cause                      text NOT NULL CHECK (cause IN ('EmployeeControlled', 'Machine', 'IncomingMaterial', 'UpstreamProcess', 'Unknown', 'Mixed')),
    employee_opportunity       boolean NOT NULL,
    failed_quantity            numeric(18, 6) NULL CHECK (failed_quantity IS NULL OR failed_quantity >= 0),
    evidence                   text NOT NULL CHECK (length(btrim(evidence)) BETWEEN 1 AND 2000),
    state                      text NOT NULL CHECK (state IN ('Proposed', 'Verified', 'Rejected', 'Superseded')),
    proposed_by                text NOT NULL,
    proposed_at                timestamptz NOT NULL,
    reviewed_by                text NULL,
    reviewed_at                timestamptz NULL,
    supersedes_attribution_id  bigint NULL REFERENCES quality.first_pass_attribution(attribution_id),
    CHECK (state = 'Proposed' OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL)),
    CHECK (reviewed_by IS NULL OR reviewed_by <> proposed_by)
);

CREATE INDEX first_pass_attribution_inspection ON quality.first_pass_attribution (inspection_id, attribution_id);
-- At most one live (proposed or verified) attribution per inspection.
CREATE UNIQUE INDEX first_pass_attribution_live ON quality.first_pass_attribution (inspection_id)
    WHERE state IN ('Proposed', 'Verified') AND supersedes_attribution_id IS NULL;
CREATE UNIQUE INDEX first_pass_attribution_one_correction ON quality.first_pass_attribution (supersedes_attribution_id)
    WHERE supersedes_attribution_id IS NOT NULL AND state IN ('Proposed', 'Verified');

CREATE FUNCTION quality.keep_first_pass_attribution() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.inspection_id, NEW.cause, NEW.employee_opportunity, NEW.failed_quantity, NEW.evidence, NEW.proposed_by, NEW.proposed_at, NEW.supersedes_attribution_id)
       IS DISTINCT FROM (OLD.inspection_id, OLD.cause, OLD.employee_opportunity, OLD.failed_quantity, OLD.evidence, OLD.proposed_by, OLD.proposed_at, OLD.supersedes_attribution_id) THEN
        RAISE EXCEPTION 'attribution % keeps its recorded facts; propose a correction instead', OLD.attribution_id;
    END IF;
    IF NOT ((OLD.state = 'Proposed' AND NEW.state IN ('Verified', 'Rejected')) OR (OLD.state = 'Verified' AND NEW.state = 'Superseded')) THEN
        RAISE EXCEPTION 'attribution % cannot move from % to %', OLD.attribution_id, OLD.state, NEW.state;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER first_pass_attribution_kept BEFORE UPDATE ON quality.first_pass_attribution
    FOR EACH ROW EXECUTE FUNCTION quality.keep_first_pass_attribution();
