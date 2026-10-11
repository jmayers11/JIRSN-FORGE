CREATE TABLE execution.shift_checklist_item (
    work_center_code text NOT NULL REFERENCES resources.work_center(code),
    code text NOT NULL,
    description text NOT NULL,
    required boolean NOT NULL,
    sort_order integer NOT NULL,
    active boolean NOT NULL DEFAULT true,
    changed_at timestamptz NOT NULL,
    changed_by text NOT NULL,
    PRIMARY KEY (work_center_code, code)
);

CREATE TABLE execution.shift_handoff (
    handoff_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    work_center_code text NOT NULL REFERENCES resources.work_center(code),
    shift_code text NOT NULL REFERENCES resources.shift(code),
    shift_from timestamptz NOT NULL,
    shift_until timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    context text NULL,
    reviewed_at timestamptz NULL,
    reviewed_by text NULL,
    source_snapshot jsonb NOT NULL,
    checklist_snapshot jsonb NOT NULL,
    UNIQUE (work_center_code, shift_code, shift_from, shift_until),
    CHECK (shift_until > shift_from),
    CHECK ((reviewed_at IS NULL) = (reviewed_by IS NULL))
);
CREATE INDEX shift_handoff_recent ON execution.shift_handoff (work_center_code, shift_from DESC, handoff_id DESC);

CREATE TABLE execution.shift_handoff_ack (
    handoff_id bigint NOT NULL REFERENCES execution.shift_handoff(handoff_id),
    person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    acknowledged_at timestamptz NOT NULL,
    note text NULL,
    PRIMARY KEY (handoff_id, person_id)
);

CREATE TABLE execution.shift_handoff_action (
    action_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    handoff_id bigint NOT NULL REFERENCES execution.shift_handoff(handoff_id),
    description text NOT NULL,
    source_kind text NULL,
    source_reference text NULL,
    assignee uuid NOT NULL REFERENCES kernel.person(person_id),
    program_id uuid NULL REFERENCES programs.program(program_id),
    status text NOT NULL CHECK (status IN ('Assigned', 'Acknowledged', 'InProgress', 'Completed')),
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    acknowledged_at timestamptz NULL,
    acknowledged_by text NULL,
    in_progress_at timestamptz NULL,
    in_progress_by text NULL,
    completed_at timestamptz NULL,
    completed_by text NULL,
    CHECK ((acknowledged_at IS NULL) = (acknowledged_by IS NULL)),
    CHECK ((in_progress_at IS NULL) = (in_progress_by IS NULL)),
    CHECK ((completed_at IS NULL) = (completed_by IS NULL)),
    CHECK ((status IN ('Assigned')) = (acknowledged_at IS NULL)),
    CHECK ((status = 'Completed') = (completed_at IS NOT NULL))
);
CREATE INDEX shift_handoff_action_open ON execution.shift_handoff_action (assignee, status, action_id);

CREATE TABLE execution.shift_closeout (
    closeout_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    work_center_code text NOT NULL REFERENCES resources.work_center(code),
    shift_code text NOT NULL REFERENCES resources.shift(code),
    shift_from timestamptz NOT NULL,
    shift_until timestamptz NOT NULL,
    closed_at timestamptz NOT NULL,
    closed_by text NOT NULL,
    facts jsonb NOT NULL,
    note text NULL,
    UNIQUE (work_center_code, shift_code, shift_from, shift_until),
    CHECK (shift_until > shift_from)
);
CREATE INDEX shift_closeout_recent ON execution.shift_closeout (work_center_code, shift_from DESC, closeout_id DESC);
