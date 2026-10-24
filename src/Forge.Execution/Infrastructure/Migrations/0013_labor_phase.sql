-- Sprint 10: retain the phase of work for each actual labor interval.
ALTER TABLE execution.labor
    ADD COLUMN phase text NOT NULL DEFAULT 'Run'
    CHECK (phase IN ('Setup', 'Run', 'Inspection', 'Rework', 'Training'));

UPDATE execution.labor
SET phase = 'Training'
WHERE role IN ('Trainee', 'Trainer');

CREATE FUNCTION execution.keep_labor_phase() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.phase IS DISTINCT FROM OLD.phase THEN
        RAISE EXCEPTION 'labor % keeps its recorded phase', OLD.labor_id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER labor_phase_kept BEFORE UPDATE ON execution.labor
    FOR EACH ROW EXECUTE FUNCTION execution.keep_labor_phase();
