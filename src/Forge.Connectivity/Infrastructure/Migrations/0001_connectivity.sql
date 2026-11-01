-- Owner decisions 2026-11-01: machine connections a plant chooses per machine (MTConnect, OPC UA, MQTT, Modbus TCP, file drop, manual,
-- simulator), what Forge last heard from each machine, and the changes of state and count it reported.
CREATE SCHEMA IF NOT EXISTS connectivity;

CREATE TABLE connectivity.machine_connection (
    machine_code        text PRIMARY KEY REFERENCES resources.machine(code),
    kind                text NOT NULL CHECK (kind IN ('MTConnect', 'OpcUa', 'Mqtt', 'ModbusTcp', 'FileDrop', 'Manual', 'Simulator')),
    settings            jsonb NOT NULL DEFAULT '{}',
    poll_seconds        integer NOT NULL CHECK (poll_seconds BETWEEN 2 AND 3600),
    stop_after_seconds  integer NOT NULL CHECK (stop_after_seconds BETWEEN 0 AND 3600),
    active              boolean NOT NULL,
    updated_by          text NOT NULL,
    updated_at          timestamptz NOT NULL
);

CREATE TABLE connectivity.machine_status (
    machine_code   text PRIMARY KEY REFERENCES resources.machine(code),
    state          text NOT NULL CHECK (state IN ('Running', 'Idle', 'Stopped', 'Alarm', 'Offline')),
    since          timestamptz NOT NULL,
    last_seen      timestamptz NULL,
    part_count     bigint NULL,
    scrap_count    bigint NULL,
    alarm          text NULL,
    last_error     text NULL,
    stop_reported  boolean NOT NULL DEFAULT false
);

CREATE TABLE connectivity.machine_reading (
    reading_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    machine_code  text NOT NULL REFERENCES resources.machine(code),
    at            timestamptz NOT NULL,
    state         text NOT NULL CHECK (state IN ('Running', 'Idle', 'Stopped', 'Alarm', 'Offline')),
    part_count    bigint NULL,
    scrap_count   bigint NULL,
    alarm         text NULL
);
CREATE INDEX machine_reading_at ON connectivity.machine_reading (machine_code, at);
