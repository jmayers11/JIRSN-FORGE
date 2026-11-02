-- Owner decisions 2026-11-02: demand forecasting. History is shipments plus imported months plus (until real history exists) simulated
-- months, each marked by source; a run keeps every part's forecast; a planner approves a part into forecast demand.
CREATE TABLE planning.demand_history (
    part_number  text NOT NULL REFERENCES engineering.item(part_number),
    month        date NOT NULL CHECK (extract(day FROM month) = 1),
    quantity     numeric(18,4) NOT NULL CHECK (quantity >= 0),
    source       text NOT NULL CHECK (source IN ('Simulated', 'Imported')),
    recorded_by  text NOT NULL,
    recorded_at  timestamptz NOT NULL,
    PRIMARY KEY (part_number, month, source)
);

CREATE TABLE planning.forecast_policy (
    policy_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    horizon           integer NOT NULL CHECK (horizon BETWEEN 1 AND 24),
    holdout           integer NOT NULL CHECK (holdout BETWEEN 1 AND 24),
    interval_percent  integer NOT NULL CHECK (interval_percent IN (80, 90, 95)),
    use_simulated     boolean NOT NULL,
    set_by            text NOT NULL,
    set_at            timestamptz NOT NULL
);

CREATE TABLE planning.forecast_run (
    run_id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_at            timestamptz NOT NULL,
    run_by            text NOT NULL,
    simulated         boolean NOT NULL,
    horizon           integer NOT NULL,
    holdout           integer NOT NULL,
    interval_percent  integer NOT NULL,
    first_month       date NOT NULL
);

CREATE TABLE planning.forecast_line (
    run_id       bigint NOT NULL REFERENCES planning.forecast_run(run_id),
    part_number  text NOT NULL,
    simulated    boolean NOT NULL,
    forecast     jsonb NOT NULL,
    approved_by  text NULL,
    approved_at  timestamptz NULL,
    PRIMARY KEY (run_id, part_number)
);
