-- Sprint 10 §22: supervisor-approved coverage assignments outside a worker's usual work, with the Section 21 forecast captured at
-- approval and a separate, versioned goal. Actual output is never stored here; it is read from production reports and inspections.
CREATE TABLE workforce.coverage_assignment (
    assignment_id           uuid PRIMARY KEY,
    person_id               uuid NOT NULL REFERENCES kernel.person(person_id),
    origin_work_center      text NULL,
    destination_work_center text NOT NULL,
    machine_code            text NULL,
    activity_code           text NOT NULL,
    order_number            bigint NULL,
    sequence                integer NULL,
    planned_start           timestamptz NOT NULL,
    planned_end             timestamptz NOT NULL,
    actual_start            timestamptz NULL,
    actual_end              timestamptz NULL,
    reason                  text NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 800),
    forecast_conservative   numeric(18, 4) NULL,
    forecast_expected       numeric(18, 4) NULL,
    forecast_optimistic     numeric(18, 4) NULL,
    forecast_confidence     text NULL,
    forecast_basis          text NULL,
    state                   text NOT NULL CHECK (state IN ('Planned', 'Started', 'Completed', 'Cancelled')),
    comparable              boolean NOT NULL DEFAULT true,
    exclusion_reason        text NULL,
    reviewed_by             text NULL,
    reviewed_at             timestamptz NULL,
    approved_by             text NOT NULL,
    approved_at             timestamptz NOT NULL,
    CHECK (planned_end > planned_start),
    CHECK ((order_number IS NULL) = (sequence IS NULL)),
    CHECK (actual_end IS NULL OR (actual_start IS NOT NULL AND actual_end > actual_start)),
    CHECK (comparable OR exclusion_reason IS NOT NULL)
);
CREATE INDEX coverage_assignment_person ON workforce.coverage_assignment (person_id, planned_start);

-- Every goal ever set is kept: the original, revisions after work began, and reviewed time-adjusted goals.
CREATE TABLE workforce.coverage_goal (
    goal_id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    assignment_id              uuid NOT NULL REFERENCES workforce.coverage_assignment(assignment_id),
    version                    integer NOT NULL CHECK (version >= 1),
    kind                       text NOT NULL CHECK (kind IN ('Original', 'Revision', 'TimeAdjusted')),
    good_units                 numeric(18, 4) NOT NULL CHECK (good_units > 0),
    planned_productive_minutes integer NOT NULL CHECK (planned_productive_minutes > 0),
    rationale                  text NOT NULL CHECK (length(btrim(rationale)) BETWEEN 1 AND 800),
    after_start                boolean NOT NULL,
    set_by                     text NOT NULL,
    set_at                     timestamptz NOT NULL,
    UNIQUE (assignment_id, version)
);

CREATE FUNCTION workforce.keep_coverage_goal() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'coverage goal % is history; set a new version instead', OLD.goal_id;
END $$;

CREATE TRIGGER coverage_goal_kept BEFORE UPDATE OR DELETE ON workforce.coverage_goal
    FOR EACH ROW EXECUTE FUNCTION workforce.keep_coverage_goal();
