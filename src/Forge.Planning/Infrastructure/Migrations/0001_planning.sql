CREATE SCHEMA IF NOT EXISTS planning;

-- Every schedule run is kept: the proposal people reviewed and the one that was approved.
CREATE TABLE planning.schedule_version (
    version_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    status              text NOT NULL CHECK (status IN ('Proposed', 'Approved', 'Superseded', 'Discarded')),
    horizon_from        timestamptz NOT NULL,
    horizon_to          timestamptz NOT NULL,
    engine_version      text NOT NULL,
    input_fingerprint   text NOT NULL,
    output_fingerprint  text NOT NULL,
    result              text NOT NULL,
    summary             text NOT NULL,
    proposed_by         text NOT NULL,
    proposed_at         timestamptz NOT NULL,
    approved_by         text NULL,
    approved_at         timestamptz NULL,
    CHECK (approved_by IS NULL OR approved_by <> proposed_by),
    CHECK (status <> 'Approved' OR approved_by IS NOT NULL)
);
CREATE UNIQUE INDEX schedule_one_approved ON planning.schedule_version ((true)) WHERE status = 'Approved';
CREATE UNIQUE INDEX schedule_one_proposed ON planning.schedule_version ((true)) WHERE status = 'Proposed';

-- Constraints found by schedule runs, with a human lifecycle.
CREATE TABLE planning.constraint_finding (
    constraint_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    finding_key          text NOT NULL UNIQUE,
    category             text NOT NULL,
    subject              text NOT NULL,
    status               text NOT NULL CHECK (status IN ('Open', 'Investigating', 'Resolved', 'Verified')),
    finding              text NOT NULL,
    evidence             text NOT NULL,
    affected_orders      bigint[] NOT NULL,
    affected_operations  text[] NOT NULL,
    alternatives         text[] NOT NULL,
    window_from          timestamptz NOT NULL,
    window_until         timestamptz NOT NULL,
    first_seen           timestamptz NOT NULL,
    last_seen            timestamptz NOT NULL,
    last_version_id      bigint NOT NULL REFERENCES planning.schedule_version(version_id),
    owner                text NULL,
    resolution           text NULL,
    resolved_by          text NULL,
    resolved_at          timestamptz NULL,
    verified_by          text NULL,
    reopened_count       integer NOT NULL DEFAULT 0
);
CREATE INDEX constraint_status ON planning.constraint_finding (status, window_from);
