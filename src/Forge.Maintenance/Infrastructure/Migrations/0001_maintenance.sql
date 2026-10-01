CREATE SCHEMA IF NOT EXISTS maintenance;

CREATE TABLE maintenance.downtime (
    downtime_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    machine_code  text NOT NULL REFERENCES resources.machine(code),
    kind          text NOT NULL CHECK (kind IN ('Unplanned', 'Planned')),
    starts_at     timestamptz NOT NULL,
    ends_at       timestamptz NULL CHECK (ends_at IS NULL OR ends_at > starts_at),
    reason        text NOT NULL,
    recorded_at   timestamptz NOT NULL,
    recorded_by   text NOT NULL,
    closed_by     text NULL,
    CHECK (kind = 'Unplanned' OR ends_at IS NOT NULL)
);

-- At most one open (unrestored) outage per machine.
CREATE UNIQUE INDEX downtime_one_open ON maintenance.downtime (machine_code) WHERE ends_at IS NULL;
CREATE INDEX downtime_window ON maintenance.downtime (machine_code, starts_at);
