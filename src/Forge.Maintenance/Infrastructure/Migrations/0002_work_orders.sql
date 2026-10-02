-- Preventive maintenance plans and maintenance work orders (spec §47).
CREATE TABLE maintenance.pm_plan (
    plan_id              bigint GENERATED ALWAYS AS IDENTITY (START WITH 101) PRIMARY KEY,
    machine_code         text NOT NULL REFERENCES resources.machine(code),
    title                text NOT NULL,
    interval_days        integer NOT NULL CHECK (interval_days BETWEEN 1 AND 3650),
    duration_minutes     integer NOT NULL CHECK (duration_minutes BETWEEN 1 AND 10080),
    tasks                text[] NOT NULL CHECK (cardinality(tasks) BETWEEN 1 AND 50),
    -- Safety-critical PM: once overdue past the grace days the machine is locked out until it is done.
    lock_out_when_overdue boolean NOT NULL,
    grace_days           integer NOT NULL CHECK (grace_days BETWEEN 0 AND 90),
    active               boolean NOT NULL DEFAULT true,
    created_at           timestamptz NOT NULL,
    created_by           text NOT NULL
);

CREATE TABLE maintenance.work_order (
    work_order_id     bigint GENERATED ALWAYS AS IDENTITY (START WITH 3001) PRIMARY KEY,
    machine_code      text NOT NULL REFERENCES resources.machine(code),
    kind              text NOT NULL CHECK (kind IN ('Preventive', 'Corrective')),
    plan_id           bigint NULL REFERENCES maintenance.pm_plan(plan_id),
    title             text NOT NULL,
    description       text NULL,
    tasks             text[] NOT NULL,
    due_date          date NOT NULL,
    status            text NOT NULL CHECK (status IN ('Open', 'Scheduled', 'InProgress', 'Completed', 'Closed', 'Cancelled')),
    scheduled_start   timestamptz NULL,
    scheduled_end     timestamptz NULL CHECK (scheduled_end IS NULL OR scheduled_end > scheduled_start),
    -- The planned window this work occupies, or the outage a corrective order repairs.
    downtime_id       bigint NULL REFERENCES maintenance.downtime(downtime_id),
    created_at        timestamptz NOT NULL,
    created_by        text NOT NULL,
    started_at        timestamptz NULL,
    started_by        text NULL,
    completed_at      timestamptz NULL,
    completed_by      text NULL,
    findings          text NULL,
    closed_at         timestamptz NULL,
    closed_by         text NULL,
    cancel_reason     text NULL,
    CHECK (kind = 'Corrective' OR plan_id IS NOT NULL),
    -- The person who did the work does not sign it off.
    CHECK (closed_by IS NULL OR closed_by <> completed_by)
);
CREATE INDEX work_order_open ON maintenance.work_order (machine_code, due_date) WHERE status IN ('Open', 'Scheduled', 'InProgress', 'Completed');
-- One open preventive work order per plan at a time.
CREATE UNIQUE INDEX work_order_one_per_plan ON maintenance.work_order (plan_id) WHERE plan_id IS NOT NULL AND status IN ('Open', 'Scheduled', 'InProgress', 'Completed');
