-- V3.7: goals, their breakdown and their attainment (V3 brief A, AW; spec §42).
--
-- A goal is production (make something by a date) or delivery (get it to the customer by a date); the two are
-- never mixed. A goal may break down into smaller goals; a child's required-by date is either its own or a number
-- of days before its parent's, so moving the parent moves the children. A reforecast records, per goal, what was
-- projected at that moment; the history is never changed.

CREATE TABLE planning.goal (
    goal_id         bigint GENERATED ALWAYS AS IDENTITY (START WITH 1001) PRIMARY KEY,
    kind            text NOT NULL CHECK (kind IN ('Production', 'Delivery')),
    title           text NOT NULL,
    parent_goal_id  bigint NULL REFERENCES planning.goal(goal_id),
    order_number    bigint NULL REFERENCES execution.production_order(order_number),
    demand_id       bigint NULL REFERENCES planning.demand(demand_id),
    required_by     date NULL,
    lead_days       integer NULL CHECK (lead_days BETWEEN 0 AND 3650),
    program_id      uuid NULL,
    status          text NOT NULL CHECK (status IN ('Open', 'Closed')),
    created_at      timestamptz NOT NULL,
    created_by      text NOT NULL,
    changed_at      timestamptz NOT NULL,
    changed_by      text NOT NULL,
    CHECK (kind = 'Production' OR order_number IS NULL),
    CHECK (kind = 'Delivery' OR demand_id IS NULL),
    CHECK ((required_by IS NULL) <> (lead_days IS NULL)),
    CHECK (parent_goal_id IS NOT NULL OR required_by IS NOT NULL),
    CHECK (parent_goal_id IS NULL OR parent_goal_id <> goal_id)
);
CREATE INDEX goal_parent ON planning.goal (parent_goal_id);

CREATE TABLE planning.goal_forecast (
    forecast_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    goal_id        bigint NOT NULL REFERENCES planning.goal(goal_id),
    at             timestamptz NOT NULL,
    required_by    date NOT NULL,
    projected      timestamptz NULL,
    state          text NOT NULL,
    behind_plan    boolean NOT NULL,
    attainment     numeric(9, 4) NOT NULL,
    recorded_by    text NOT NULL
);
CREATE INDEX goal_forecast_goal ON planning.goal_forecast (goal_id, at);

CREATE FUNCTION planning.reject_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are history and are never changed', TG_TABLE_NAME;
END $$;
CREATE TRIGGER goal_forecast_append_only BEFORE UPDATE OR DELETE ON planning.goal_forecast
    FOR EACH ROW EXECUTE FUNCTION planning.reject_history_change();
