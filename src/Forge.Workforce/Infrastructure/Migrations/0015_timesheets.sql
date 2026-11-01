-- Owner decision 2026-10-31: DCAA-style timesheets. Employees record every hour daily against an order operation or a charge code,
-- sign the week, and a supervisor who is not the employee approves it. Entries keep their recorded facts; corrections supersede.
CREATE TABLE workforce.charge_code (
    code             text PRIMARY KEY CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,39}$'),
    name             text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    kind             text NOT NULL CHECK (kind IN ('Contract', 'Indirect', 'Overhead', 'GeneralAndAdministrative', 'Leave', 'Unallowable')),
    contract_number  text NULL,
    clin             text NULL,
    active           boolean NOT NULL,
    created_at       timestamptz NOT NULL,
    created_by       text NOT NULL,
    changed_at       timestamptz NULL,
    changed_by       text NULL,
    CHECK ((kind = 'Contract') = (contract_number IS NOT NULL)),
    CHECK (kind = 'Contract' OR clin IS NULL)
);

CREATE TABLE workforce.timesheet (
    timesheet_id   uuid PRIMARY KEY,
    person_id      uuid NOT NULL REFERENCES kernel.person(person_id),
    week_start     date NOT NULL CHECK (extract(isodow FROM week_start) = 1),
    status         text NOT NULL CHECK (status IN ('Open', 'Signed', 'Approved', 'Returned')),
    signed_at      timestamptz NULL,
    signed_by      text NULL,
    approved_at    timestamptz NULL,
    approved_by    text NULL,
    returned_at    timestamptz NULL,
    returned_by    text NULL,
    return_reason  text NULL,
    UNIQUE (person_id, week_start),
    CHECK (status NOT IN ('Signed', 'Approved') OR signed_by IS NOT NULL),
    CHECK (status <> 'Approved' OR (approved_by IS NOT NULL AND approved_by <> signed_by)),
    CHECK (status <> 'Returned' OR return_reason IS NOT NULL)
);

CREATE TABLE workforce.time_entry (
    entry_id             uuid PRIMARY KEY,
    timesheet_id         uuid NOT NULL REFERENCES workforce.timesheet(timesheet_id),
    work_date            date NOT NULL,
    order_number         bigint NULL,
    sequence             integer NULL,
    charge_code          text NULL REFERENCES workforce.charge_code(code),
    hours                numeric(5, 2) NOT NULL CHECK (hours > 0 AND hours <= 24 AND hours * 4 = trunc(hours * 4)),
    note                 text NULL,
    state                text NOT NULL CHECK (state IN ('Active', 'Superseded', 'Withdrawn')),
    recorded_at          timestamptz NOT NULL,
    recorded_by          text NOT NULL,
    late_reason          text NULL,
    supersedes_entry_id  uuid NULL REFERENCES workforce.time_entry(entry_id),
    correction_reason    text NULL,
    CHECK ((order_number IS NULL) = (sequence IS NULL)),
    CHECK ((order_number IS NULL) <> (charge_code IS NULL)),
    CHECK (supersedes_entry_id IS NULL OR correction_reason IS NOT NULL),
    CHECK (state <> 'Withdrawn' OR correction_reason IS NOT NULL)
);
CREATE INDEX time_entry_sheet ON workforce.time_entry (timesheet_id, work_date);
CREATE INDEX time_entry_order ON workforce.time_entry (order_number) WHERE order_number IS NOT NULL;
CREATE UNIQUE INDEX time_entry_corrected_once ON workforce.time_entry (supersedes_entry_id) WHERE supersedes_entry_id IS NOT NULL;

-- An entry's facts never change and nothing is deleted; only an active entry moves to superseded or withdrawn.
CREATE FUNCTION workforce.keep_time_entry() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'time entry % is kept; withdraw it with a correction instead', OLD.entry_id;
    END IF;
    IF (NEW.timesheet_id, NEW.work_date, NEW.order_number, NEW.sequence, NEW.charge_code, NEW.hours, NEW.note, NEW.recorded_at, NEW.recorded_by,
        NEW.late_reason, NEW.supersedes_entry_id)
       IS DISTINCT FROM (OLD.timesheet_id, OLD.work_date, OLD.order_number, OLD.sequence, OLD.charge_code, OLD.hours, OLD.note, OLD.recorded_at, OLD.recorded_by,
        OLD.late_reason, OLD.supersedes_entry_id) THEN
        RAISE EXCEPTION 'time entry % keeps its recorded facts; record a correction instead', OLD.entry_id;
    END IF;
    IF NOT (OLD.state = 'Active' AND NEW.state IN ('Superseded', 'Withdrawn')) THEN
        RAISE EXCEPTION 'time entry % cannot move from % to %', OLD.entry_id, OLD.state, NEW.state;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER time_entry_kept BEFORE UPDATE OR DELETE ON workforce.time_entry
    FOR EACH ROW EXECUTE FUNCTION workforce.keep_time_entry();
