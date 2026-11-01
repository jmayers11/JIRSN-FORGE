-- Owner decisions 2026-11-01: supplier scorecards. On time is on or before the promised date unless the company allows days late or
-- limits how early counts; grades A/B/C come from on-time and quality percentages. Versions are kept; a grade never holds a supplier.
CREATE TABLE procurement.scorecard_policy (
    policy_id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    late_tolerance_days  integer NOT NULL CHECK (late_tolerance_days BETWEEN 0 AND 30),
    early_limit_days     integer NULL CHECK (early_limit_days BETWEEN 0 AND 365),
    grade_a_on_time      numeric(5, 2) NOT NULL CHECK (grade_a_on_time BETWEEN 0 AND 100),
    grade_a_quality      numeric(5, 2) NOT NULL CHECK (grade_a_quality BETWEEN 0 AND 100),
    grade_b_on_time      numeric(5, 2) NOT NULL CHECK (grade_b_on_time BETWEEN 0 AND 100 AND grade_b_on_time <= grade_a_on_time),
    grade_b_quality      numeric(5, 2) NOT NULL CHECK (grade_b_quality BETWEEN 0 AND 100 AND grade_b_quality <= grade_a_quality),
    period_days          integer NOT NULL CHECK (period_days BETWEEN 7 AND 730),
    effective_from       timestamptz NOT NULL,
    set_by               text NOT NULL,
    reason               text NOT NULL
);
