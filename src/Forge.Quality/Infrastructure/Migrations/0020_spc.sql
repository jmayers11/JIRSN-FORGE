-- Owner decisions 2026-11-01: statistical process control. Policy versions (required Cpk, critical Cpk, points before limits), the
-- characteristics marked critical, and control-chart signals recorded once each and acknowledged by a quality engineer.
CREATE TABLE quality.spc_policy (
    policy_id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    minimum_cpk            numeric(4, 2) NOT NULL CHECK (minimum_cpk BETWEEN 0.5 AND 3),
    critical_minimum_cpk   numeric(4, 2) NOT NULL CHECK (critical_minimum_cpk BETWEEN 0.5 AND 3 AND critical_minimum_cpk >= minimum_cpk),
    minimum_points         integer NOT NULL CHECK (minimum_points BETWEEN 5 AND 100),
    notify_on_signal       boolean NOT NULL,
    effective_from         timestamptz NOT NULL,
    set_by                 text NOT NULL,
    reason                 text NOT NULL
);

CREATE TABLE quality.spc_critical_characteristic (
    part_number  text NOT NULL REFERENCES engineering.item(part_number),
    sequence     integer NOT NULL,
    number       integer NOT NULL,
    marked_at    timestamptz NOT NULL,
    marked_by    text NOT NULL,
    PRIMARY KEY (part_number, sequence, number)
);

CREATE TABLE quality.spc_signal (
    signal_id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number          text NOT NULL REFERENCES engineering.item(part_number),
    sequence             integer NOT NULL,
    number               integer NOT NULL,
    characteristic_name  text NOT NULL,
    inspection_id        bigint NOT NULL,
    order_number         bigint NOT NULL,
    rule                 text NOT NULL CHECK (rule IN ('BeyondThreeSigma', 'TwoOfThreeBeyondTwoSigma', 'FourOfFiveBeyondOneSigma', 'EightOnOneSide')),
    description          text NOT NULL,
    value                numeric(18, 6) NOT NULL,
    detected_at          timestamptz NOT NULL,
    status               text NOT NULL CHECK (status IN ('Open', 'Acknowledged')),
    acknowledged_at      timestamptz NULL,
    acknowledged_by      text NULL,
    note                 text NULL,
    UNIQUE (part_number, sequence, number, inspection_id, rule),
    CHECK ((status = 'Acknowledged') = (acknowledged_by IS NOT NULL AND note IS NOT NULL))
);
