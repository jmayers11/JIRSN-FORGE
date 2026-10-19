-- ADR 0057 §5: temporary moves and transfers are separate actions.
-- A temporary move (loan) puts a worker in another work center from a start date to a required estimated end date.
-- Dates are site dates. While it lasts, projected capacity counts the worker there. Nobody acting by the end date
-- means the worker is back home from the day after it.
-- Like every workforce table, these hold no reason column (Spec 78: nothing about why a person is or is not somewhere is
-- kept with the workforce record). The reason each command gives is in the audit trail with that command.
CREATE TABLE workforce.temporary_move (
    move_id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id         uuid NOT NULL REFERENCES workforce.worker(person_id),
    from_work_center  text NOT NULL REFERENCES resources.work_center(code),
    to_work_center    text NOT NULL REFERENCES resources.work_center(code),
    starts_on         date NOT NULL,
    ends_on           date NOT NULL CHECK (ends_on >= starts_on - 1), -- the day before the start: cancelled before it began
    status            text NOT NULL CHECK (status IN ('Active', 'EndedEarly', 'Returned')),
    created_at        timestamptz NOT NULL,
    created_by        text NOT NULL,
    reminded_for      date NULL,
    closed_at         timestamptz NULL,
    closed_by         text NULL,
    CHECK (from_work_center <> to_work_center),
    CHECK ((status = 'Active') = (closed_at IS NULL))
);
CREATE INDEX temporary_move_person ON workforce.temporary_move (person_id, starts_on);
CREATE INDEX temporary_move_active ON workforce.temporary_move (ends_on) WHERE status = 'Active';

-- Each extension or renewal records the new end date (why is in the audit trail).
CREATE TABLE workforce.temporary_move_change (
    move_id           bigint NOT NULL REFERENCES workforce.temporary_move(move_id),
    change_number     integer NOT NULL CHECK (change_number > 0),
    kind              text NOT NULL CHECK (kind IN ('Extended', 'Renewed')),
    previous_ends_on  date NOT NULL,
    ends_on           date NOT NULL,
    changed_at        timestamptz NOT NULL,
    changed_by        text NOT NULL,
    PRIMARY KEY (move_id, change_number)
);

-- A transfer permanently changes the home work center from a date. The history is kept so past capacity and labor
-- reports stay as they were; workforce.worker.home_work_center stays the home the worker was registered with.
CREATE TABLE workforce.home_history (
    transfer_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id         uuid NOT NULL REFERENCES workforce.worker(person_id),
    from_work_center  text NOT NULL REFERENCES resources.work_center(code),
    work_center       text NOT NULL REFERENCES resources.work_center(code),
    effective_from    date NOT NULL,
    recorded_at       timestamptz NOT NULL,
    recorded_by       text NOT NULL,
    UNIQUE (person_id, effective_from)
);

-- Supervisors are reminded before a move ends and told when a worker is treated as back. Administrators can change
-- or switch off these rules like any other.
INSERT INTO kernel.notification_rule (rule_id, name, trigger_event_type, clear_event_type, condition_field, condition_equals, severity, confidence, recipient_roles,
    channels, template, resolution_template, deep_link_template, dedup_seconds, created_at, created_by, updated_at, updated_by)
VALUES
    ('0b1d5a57-0057-4a01-9a01-000000000001', 'Temporary move ending soon', 'TemporaryMoveEndingSoon', 'TemporaryMoveChanged', NULL, NULL, 'Watch', 'Confirmed',
     ARRAY['Supervisor'], ARRAY['InApp'], '{message}', 'The temporary move was extended.', '#/work-centers', 0, now(), 'system:migration', now(), 'system:migration'),
    ('0b1d5a57-0057-4a01-9a01-000000000002', 'Worker back from a temporary move', 'TemporaryMoveEnded', NULL, 'outcome', 'Returned', 'Information', 'Confirmed',
     ARRAY['Supervisor'], ARRAY['InApp'], '{message}', 'Acknowledged.', '#/work-centers', 0, now(), 'system:migration', now(), 'system:migration')
ON CONFLICT DO NOTHING;
