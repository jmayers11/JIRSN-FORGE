-- V3.5: production actuals (V3 brief L, K, AT).
--
-- Reason codes say why something happened, in the plant's own words, and are kept apart from a proven root cause.
-- A stoppage is a period a machine did not run, with one category (planned, unplanned, setup, changeover,
-- maintenance, waiting, starved, blocked) and one reason code. Setup and changeover stoppages name the order
-- operation they prepare and carry the routing's expected minutes, so expected and actual changeovers compare.
-- A root cause is proposed with its evidence and becomes proven only when a different person confirms it.

CREATE TABLE execution.reason_code (
    code         text PRIMARY KEY,
    kind         text NOT NULL CHECK (kind IN ('Downtime', 'Scrap', 'Rework')),
    category     text NULL CHECK (category IN ('Planned', 'Unplanned', 'Setup', 'Changeover', 'Maintenance', 'Waiting', 'Starved', 'Blocked')),
    description  text NOT NULL,
    active       boolean NOT NULL DEFAULT true,
    changed_at   timestamptz NOT NULL,
    changed_by   text NOT NULL,
    CHECK ((kind = 'Downtime') = (category IS NOT NULL))
);

CREATE TABLE execution.stoppage (
    stoppage_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    machine_code      text NOT NULL REFERENCES resources.machine(code),
    work_center_code  text NOT NULL,
    category          text NOT NULL CHECK (category IN ('Planned', 'Unplanned', 'Setup', 'Changeover', 'Maintenance', 'Waiting', 'Starved', 'Blocked')),
    reason_code       text NOT NULL REFERENCES execution.reason_code(code),
    order_number      bigint NULL,
    sequence          integer NULL,
    expected_minutes  numeric(10, 2) NULL CHECK (expected_minutes >= 0),
    note              text NULL,
    started_at        timestamptz NOT NULL,
    started_by        text NOT NULL,
    ended_at          timestamptz NULL CHECK (ended_at IS NULL OR ended_at >= started_at),
    ended_by          text NULL,
    FOREIGN KEY (order_number, sequence) REFERENCES execution.order_operation(order_number, sequence),
    CHECK ((order_number IS NULL) = (sequence IS NULL)),
    CHECK (category NOT IN ('Setup', 'Changeover') OR order_number IS NOT NULL),
    CHECK ((ended_at IS NULL) = (ended_by IS NULL))
);
-- One open stoppage per machine: a repeated report cannot open a second.
CREATE UNIQUE INDEX stoppage_one_open ON execution.stoppage (machine_code) WHERE ended_at IS NULL;
CREATE INDEX stoppage_period ON execution.stoppage (started_at);

-- A stoppage is ended once; nothing else about it ever changes.
CREATE FUNCTION execution.stoppage_end_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR OLD.ended_at IS NOT NULL
       OR (NEW.stoppage_id, NEW.machine_code, NEW.work_center_code, NEW.category, NEW.reason_code, NEW.order_number, NEW.sequence, NEW.expected_minutes, NEW.note, NEW.started_at, NEW.started_by)
          IS DISTINCT FROM (OLD.stoppage_id, OLD.machine_code, OLD.work_center_code, OLD.category, OLD.reason_code, OLD.order_number, OLD.sequence, OLD.expected_minutes, OLD.note, OLD.started_at, OLD.started_by) THEN
        RAISE EXCEPTION 'stoppage % is only ever ended, once', OLD.stoppage_id;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER stoppage_end_only BEFORE UPDATE OR DELETE ON execution.stoppage
    FOR EACH ROW EXECUTE FUNCTION execution.stoppage_end_only();

CREATE TABLE execution.root_cause (
    root_cause_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    stoppage_id    bigint NOT NULL REFERENCES execution.stoppage(stoppage_id),
    cause          text NOT NULL,
    evidence       text NOT NULL,
    proposed_at    timestamptz NOT NULL,
    proposed_by    text NOT NULL,
    confirmed_at   timestamptz NULL,
    confirmed_by   text NULL,
    CHECK ((confirmed_at IS NULL) = (confirmed_by IS NULL)),
    CHECK (confirmed_by IS NULL OR confirmed_by <> proposed_by)
);
CREATE INDEX root_cause_stoppage ON execution.root_cause (stoppage_id);

-- A root cause is confirmed once; what was proposed never changes.
CREATE FUNCTION execution.root_cause_confirm_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR OLD.confirmed_at IS NOT NULL
       OR (NEW.root_cause_id, NEW.stoppage_id, NEW.cause, NEW.evidence, NEW.proposed_at, NEW.proposed_by)
          IS DISTINCT FROM (OLD.root_cause_id, OLD.stoppage_id, OLD.cause, OLD.evidence, OLD.proposed_at, OLD.proposed_by) THEN
        RAISE EXCEPTION 'root cause % is only ever confirmed, once', OLD.root_cause_id;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER root_cause_confirm_only BEFORE UPDATE OR DELETE ON execution.root_cause
    FOR EACH ROW EXECUTE FUNCTION execution.root_cause_confirm_only();

-- Scrap reported on the shop floor may name a scrap reason code; the free-text reason stays as recorded.
ALTER TABLE execution.production_report ADD COLUMN scrap_reason_code text NULL REFERENCES execution.reason_code(code);
