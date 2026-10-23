CREATE TABLE workforce.attendance_policy (
    policy_code text NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    effective_from timestamptz NOT NULL,
    effective_through timestamptz NULL,
    grace_minutes integer NOT NULL CHECK (grace_minutes BETWEEN 0 AND 240),
    include_callouts_in_arrival_denominator boolean NOT NULL DEFAULT false,
    recorded_at timestamptz NOT NULL,
    recorded_by text NOT NULL,
    PRIMARY KEY (policy_code, revision),
    CHECK (effective_through IS NULL OR effective_through > effective_from)
);

CREATE TABLE workforce.attendance_opportunity (
    opportunity_id uuid PRIMARY KEY,
    person_id uuid NOT NULL REFERENCES workforce.worker(person_id),
    shift_code text NOT NULL,
    scheduled_start timestamptz NOT NULL,
    scheduled_end timestamptz NOT NULL,
    state text NOT NULL CHECK (state IN ('Scheduled', 'Cancelled')),
    source text NOT NULL,
    source_key text NOT NULL UNIQUE,
    supersedes_opportunity_id uuid NULL REFERENCES workforce.attendance_opportunity(opportunity_id),
    recorded_at timestamptz NOT NULL,
    recorded_by text NOT NULL,
    CHECK (scheduled_end > scheduled_start),
    UNIQUE (opportunity_id, person_id)
);
CREATE INDEX attendance_opportunity_period ON workforce.attendance_opportunity(scheduled_start, scheduled_end, person_id);

CREATE TABLE workforce.attendance_intelligence_event (
    event_id uuid PRIMARY KEY,
    opportunity_id uuid NOT NULL REFERENCES workforce.attendance_opportunity(opportunity_id),
    person_id uuid NOT NULL REFERENCES workforce.worker(person_id),
    kind text NOT NULL CHECK (kind IN ('Arrival', 'CallOut', 'NoShow', 'ApprovedPto', 'ProtectedLeave', 'ExcusedAbsence', 'EarlyDeparture', 'ScheduleChange', 'ShiftCancelled')),
    observed_at timestamptz NULL,
    review_state text NOT NULL CHECK (review_state IN ('PendingReview', 'Verified', 'Disputed', 'Superseded')),
    source text NOT NULL CHECK (source IN ('Manual', 'Nexus', 'Timeclock', 'Hris')),
    recorded_at timestamptz NOT NULL,
    recorded_by text NOT NULL,
    supersedes_event_id uuid NULL REFERENCES workforce.attendance_intelligence_event(event_id),
    reason_code text NULL,
    FOREIGN KEY (opportunity_id, person_id) REFERENCES workforce.attendance_opportunity(opportunity_id, person_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX attendance_intelligence_events_opportunity ON workforce.attendance_intelligence_event(opportunity_id, recorded_at, event_id);
