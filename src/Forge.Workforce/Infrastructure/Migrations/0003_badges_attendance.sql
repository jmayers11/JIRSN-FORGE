-- Badges: a digest of the badge value, never the value itself. PINs are PBKDF2 hashes.
CREATE TABLE workforce.badge (
    badge_digest  text PRIMARY KEY,
    person_id     uuid NOT NULL REFERENCES kernel.person(person_id),
    issued_at     timestamptz NOT NULL,
    issued_by     text NOT NULL,
    revoked_at    timestamptz NULL,
    revoked_by    text NULL,
    pin_hash      bytea NULL,
    pin_salt      bytea NULL,
    pin_set_at    timestamptz NULL,
    CHECK ((pin_hash IS NULL) = (pin_salt IS NULL))
);
-- One active badge per person.
CREATE UNIQUE INDEX badge_one_active ON workforce.badge (person_id) WHERE revoked_at IS NULL;

-- Attendance: paid time on site. Not direct labor, not operation authentication, not signoff.
CREATE TABLE workforce.attendance (
    attendance_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id      uuid NOT NULL REFERENCES workforce.worker(person_id),
    clock_in_at    timestamptz NOT NULL,
    clock_in_by    text NOT NULL,
    clock_out_at   timestamptz NULL CHECK (clock_out_at IS NULL OR clock_out_at >= clock_in_at),
    clock_out_by   text NULL
);
-- A second scan cannot open a second shift.
CREATE UNIQUE INDEX attendance_one_open ON workforce.attendance (person_id) WHERE clock_out_at IS NULL;
CREATE INDEX attendance_period ON workforce.attendance (clock_in_at, clock_out_at);
