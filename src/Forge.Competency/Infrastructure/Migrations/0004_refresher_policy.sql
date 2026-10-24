-- Sprint 10 §21.1: a site rule per qualification type. When nobody has operated under the qualification for longer than
-- refresher_after_days, independent assignment needs a passed practical assessment again (a hard stop); reacclimation_minutes
-- is extra setup time in capacity estimates for someone who passed it. Rows are versions; the latest applies.
CREATE TABLE competency.refresher_policy (
    policy_id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code                  text NOT NULL REFERENCES competency.qualification_type(code),
    refresher_after_days  integer NULL CHECK (refresher_after_days IS NULL OR refresher_after_days BETWEEN 1 AND 3650),
    reacclimation_minutes integer NULL CHECK (reacclimation_minutes IS NULL OR reacclimation_minutes BETWEEN 0 AND 960),
    set_by                text NOT NULL,
    set_at                timestamptz NOT NULL
);
CREATE INDEX refresher_policy_code ON competency.refresher_policy (code, policy_id);
