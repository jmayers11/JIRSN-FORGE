-- Shop-floor execution: direct labor, quantities, supervised training and electronic signoff.
ALTER TABLE execution.production_order DROP CONSTRAINT production_order_status_check;
ALTER TABLE execution.production_order ADD CONSTRAINT production_order_status_check
    CHECK (status IN ('Planned', 'FirmPlanned', 'Released', 'Held', 'Cancelled', 'Completed'));
ALTER TABLE execution.production_order ADD COLUMN completed_at timestamptz NULL;

ALTER TABLE execution.order_operation
    ADD COLUMN good_quantity   numeric(18, 6) NOT NULL DEFAULT 0 CHECK (good_quantity >= 0),
    ADD COLUMN scrap_quantity  numeric(18, 6) NOT NULL DEFAULT 0 CHECK (scrap_quantity >= 0),
    ADD COLUMN started_at      timestamptz NULL,
    ADD COLUMN completed_at    timestamptz NULL;

-- Supervised training: a trainee works under a different, present trainer. It grants nothing.
CREATE TABLE execution.training_session (
    session_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    trainee_id        uuid NOT NULL REFERENCES workforce.worker(person_id),
    trainer_id        uuid NOT NULL REFERENCES workforce.worker(person_id),
    qualifications    text[] NOT NULL,
    order_number      bigint NOT NULL,
    sequence          integer NOT NULL,
    machine_code      text NULL,
    started_at        timestamptz NOT NULL,
    ended_at          timestamptz NULL,
    evaluation        text NULL CHECK (evaluation IN ('NeedsMoreTraining', 'ReadyForAssessment')),
    evaluated_at      timestamptz NULL,
    evaluation_notes  text NULL,
    FOREIGN KEY (order_number, sequence) REFERENCES execution.order_operation(order_number, sequence),
    CHECK (trainee_id <> trainer_id),
    CHECK ((evaluation IS NULL) = (evaluated_at IS NULL))
);

-- Direct labor: who was on which operation, in which role, from when to when.
CREATE TABLE execution.labor (
    labor_id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id            uuid NOT NULL REFERENCES workforce.worker(person_id),
    order_number         bigint NOT NULL,
    sequence             integer NOT NULL,
    work_center_code     text NOT NULL,
    machine_code         text NULL,
    role                 text NOT NULL CHECK (role IN ('Operator', 'Trainee', 'Trainer')),
    started_at           timestamptz NOT NULL,
    ended_at             timestamptz NULL CHECK (ended_at IS NULL OR ended_at >= started_at),
    training_session_id  bigint NULL REFERENCES execution.training_session(session_id),
    recorded_by          text NOT NULL,
    FOREIGN KEY (order_number, sequence) REFERENCES execution.order_operation(order_number, sequence),
    CHECK ((role = 'Operator') = (training_session_id IS NULL))
);
-- A person is on one operation at a time; a repeated scan cannot open a second entry.
CREATE UNIQUE INDEX labor_one_open ON execution.labor (person_id) WHERE ended_at IS NULL;
CREATE INDEX labor_period ON execution.labor (started_at, ended_at);

CREATE TABLE execution.production_report (
    report_id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number        bigint NOT NULL,
    sequence            integer NOT NULL,
    work_center_code    text NOT NULL,
    person_id           uuid NOT NULL REFERENCES workforce.worker(person_id),
    good_quantity       numeric(18, 6) NOT NULL CHECK (good_quantity >= 0),
    scrap_quantity      numeric(18, 6) NOT NULL CHECK (scrap_quantity >= 0),
    scrap_reason        text NULL,
    completed_operation boolean NOT NULL,
    final_operation     boolean NOT NULL,
    reported_at         timestamptz NOT NULL,
    recorded_by         text NOT NULL,
    FOREIGN KEY (order_number, sequence) REFERENCES execution.order_operation(order_number, sequence),
    CHECK (scrap_quantity = 0 OR scrap_reason IS NOT NULL)
);
CREATE INDEX production_report_time ON execution.production_report (reported_at);

-- Electronic signoff: identity, method, action, subject, time and the terminal it came through.
CREATE TABLE execution.electronic_signoff (
    signoff_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id     uuid NOT NULL REFERENCES kernel.person(person_id),
    method        text NOT NULL CHECK (method IN ('Badge', 'BadgeAndPin')),
    action        text NOT NULL,
    subject       text NOT NULL,
    meaning       text NOT NULL,
    signed_at     timestamptz NOT NULL,
    recorded_by   text NOT NULL
);

-- History is append-only.
CREATE FUNCTION execution.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are append-only', TG_TABLE_NAME;
END $$;
CREATE TRIGGER production_report_append_only BEFORE UPDATE OR DELETE ON execution.production_report
    FOR EACH ROW EXECUTE FUNCTION execution.forbid_change();
CREATE TRIGGER electronic_signoff_append_only BEFORE UPDATE OR DELETE ON execution.electronic_signoff
    FOR EACH ROW EXECUTE FUNCTION execution.forbid_change();
