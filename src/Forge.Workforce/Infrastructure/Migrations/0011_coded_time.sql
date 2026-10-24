-- Sprint 10 §16.1: coded intervals of paid time that no other record covers (breaks, meetings, indirect work, waiting).
-- Operation labor, machine downtime and stoppages stay in their own contexts and are not duplicated here.
CREATE TABLE workforce.coded_time (
    entry_id            uuid PRIMARY KEY,
    person_id           uuid NOT NULL REFERENCES kernel.person(person_id),
    starts_at           timestamptz NOT NULL,
    ends_at             timestamptz NOT NULL,
    code                text NOT NULL CHECK (code IN ('Break', 'Meeting', 'IndirectTask', 'Cleaning', 'Training', 'MaterialShortage',
                                                      'WaitingForInspection', 'QualityHold', 'MachineDowntime', 'Other')),
    paid                boolean NOT NULL,
    work_center_code    text NULL,
    machine_code        text NULL,
    order_number        bigint NULL,
    sequence            integer NULL,
    note                text NOT NULL CHECK (length(btrim(note)) BETWEEN 1 AND 800),
    state               text NOT NULL CHECK (state IN ('PendingReview', 'Approved', 'Rejected', 'Superseded')),
    recorded_by         text NOT NULL,
    recorded_at         timestamptz NOT NULL,
    reviewed_by         text NULL,
    reviewed_at         timestamptz NULL,
    supersedes_entry_id uuid NULL REFERENCES workforce.coded_time(entry_id),
    CHECK (ends_at > starts_at AND ends_at - starts_at <= interval '16 hours'),
    CHECK ((order_number IS NULL) = (sequence IS NULL)),
    CHECK (state = 'PendingReview' OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL)),
    CHECK (reviewed_by IS NULL OR reviewed_by <> recorded_by)
);

CREATE INDEX coded_time_person ON workforce.coded_time (person_id, starts_at);
CREATE UNIQUE INDEX coded_time_one_correction ON workforce.coded_time (supersedes_entry_id)
    WHERE supersedes_entry_id IS NOT NULL AND state IN ('PendingReview', 'Approved');

-- An entry's facts never change; only its review state moves forward.
CREATE FUNCTION workforce.keep_coded_time() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.person_id, NEW.starts_at, NEW.ends_at, NEW.code, NEW.paid, NEW.note, NEW.recorded_by, NEW.recorded_at,
        NEW.work_center_code, NEW.machine_code, NEW.order_number, NEW.sequence, NEW.supersedes_entry_id)
       IS DISTINCT FROM (OLD.person_id, OLD.starts_at, OLD.ends_at, OLD.code, OLD.paid, OLD.note, OLD.recorded_by, OLD.recorded_at,
        OLD.work_center_code, OLD.machine_code, OLD.order_number, OLD.sequence, OLD.supersedes_entry_id) THEN
        RAISE EXCEPTION 'coded time % keeps its recorded facts; record a correction instead', OLD.entry_id;
    END IF;
    IF NOT ((OLD.state = 'PendingReview' AND NEW.state IN ('Approved', 'Rejected')) OR (OLD.state = 'Approved' AND NEW.state = 'Superseded')) THEN
        RAISE EXCEPTION 'coded time % cannot move from % to %', OLD.entry_id, OLD.state, NEW.state;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER coded_time_kept BEFORE UPDATE ON workforce.coded_time
    FOR EACH ROW EXECUTE FUNCTION workforce.keep_coded_time();
