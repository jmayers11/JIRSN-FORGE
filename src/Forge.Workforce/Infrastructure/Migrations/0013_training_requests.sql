-- New-employee training and training requests. Onboarding requirements say which qualifications every new hire (or every new hire
-- in a work center) must be trained on, and how many days they have. A training request is one person needing one piece of training
-- scheduled: from onboarding, an expiring qualification, a supervisor or an approved development packet. Technical trainers schedule
-- and deliver it; certification stays in Competency.
CREATE TABLE workforce.onboarding_requirement (
    requirement_id      uuid PRIMARY KEY,
    work_center_code    text NULL REFERENCES resources.work_center(code),
    qualification_code  text NOT NULL,
    due_days            integer NOT NULL CHECK (due_days BETWEEN 1 AND 365),
    note                text NOT NULL DEFAULT '',
    active              boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL,
    created_by          text NOT NULL,
    updated_at          timestamptz NOT NULL,
    updated_by          text NOT NULL
);

CREATE UNIQUE INDEX onboarding_requirement_active ON workforce.onboarding_requirement (COALESCE(work_center_code, ''), qualification_code) WHERE active;

CREATE TABLE workforce.training_request (
    request_id             uuid PRIMARY KEY,
    person_id              uuid NOT NULL REFERENCES workforce.worker(person_id),
    source                 text NOT NULL CHECK (source IN ('Onboarding', 'Expiry', 'Supervisor', 'Development')),
    source_key             text NOT NULL,
    qualification_code     text NULL,
    title                  text NOT NULL CHECK (length(btrim(title)) BETWEEN 1 AND 200),
    reason                 text NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 800),
    due_on                 date NULL,
    status                 text NOT NULL CHECK (status IN ('Requested', 'Scheduled', 'Completed', 'Cancelled')),
    trainer_person_id      uuid NULL REFERENCES kernel.person(person_id),
    scheduled_start        timestamptz NULL,
    scheduled_end          timestamptz NULL,
    location               text NULL,
    outcome_note           text NULL,
    requested_by           text NOT NULL,
    requested_by_person_id uuid NULL REFERENCES kernel.person(person_id),
    requested_at           timestamptz NOT NULL,
    updated_at             timestamptz NOT NULL,
    version                integer NOT NULL CHECK (version > 0),
    CHECK ((scheduled_start IS NULL) = (scheduled_end IS NULL)),
    CHECK (scheduled_end IS NULL OR scheduled_end > scheduled_start),
    UNIQUE (person_id, source, source_key)
);

CREATE INDEX training_request_open ON workforce.training_request (status, due_on, requested_at) WHERE status IN ('Requested', 'Scheduled');
CREATE INDEX training_request_person ON workforce.training_request (person_id, requested_at DESC);

CREATE TABLE workforce.training_request_event (
    event_id              uuid PRIMARY KEY,
    request_id            uuid NOT NULL REFERENCES workforce.training_request(request_id),
    from_status           text NULL,
    to_status             text NOT NULL,
    note                  text NOT NULL,
    recorded_by           text NOT NULL,
    recorded_by_person_id uuid NULL REFERENCES kernel.person(person_id),
    recorded_at           timestamptz NOT NULL
);

CREATE INDEX training_request_event_history ON workforce.training_request_event (request_id, recorded_at, event_id);
