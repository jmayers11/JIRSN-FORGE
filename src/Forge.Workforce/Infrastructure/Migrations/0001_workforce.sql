CREATE SCHEMA IF NOT EXISTS workforce;

CREATE TABLE workforce.worker (
    person_id          uuid PRIMARY KEY REFERENCES kernel.person(person_id),
    display_name       text NOT NULL,
    home_work_center   text NOT NULL REFERENCES resources.work_center(code),
    created_at         timestamptz NOT NULL,
    created_by         text NOT NULL
);

-- One current assignment per worker. History lives in the audit and event logs.
CREATE TABLE workforce.assignment (
    person_id         uuid PRIMARY KEY REFERENCES workforce.worker(person_id),
    safety_area_code  text NOT NULL REFERENCES resources.safety_area(code),
    work_center_code  text NULL REFERENCES resources.work_center(code),
    machine_code      text NULL REFERENCES resources.machine(code),
    activity_code     text NOT NULL REFERENCES resources.activity_type(code),
    as_trainee        boolean NOT NULL,
    assigned_at       timestamptz NOT NULL,
    assigned_by       text NOT NULL
);

-- Evidence that a person is on site. It expires; a schedule never creates it.
CREATE TABLE workforce.presence (
    presence_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id    uuid NOT NULL REFERENCES workforce.worker(person_id),
    kind         text NOT NULL CHECK (kind IN ('ClockedIn', 'SupervisorConfirmed')),
    recorded_at  timestamptz NOT NULL,
    valid_until  timestamptz NOT NULL CHECK (valid_until > recorded_at),
    recorded_by  text NOT NULL
);
CREATE INDEX presence_person ON workforce.presence (person_id, valid_until);

-- Operational unavailability. There is intentionally no reason column.
CREATE TABLE workforce.unavailability (
    unavailability_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id          uuid NOT NULL REFERENCES workforce.worker(person_id),
    kind               text NOT NULL CHECK (kind IN ('CallOut', 'Unavailable', 'ApprovedPto', 'Training')),
    starts_at          timestamptz NOT NULL,
    ends_at            timestamptz NOT NULL CHECK (ends_at > starts_at),
    recorded_at        timestamptz NOT NULL,
    recorded_by        text NOT NULL
);
CREATE INDEX unavailability_person ON workforce.unavailability (person_id, starts_at, ends_at);
