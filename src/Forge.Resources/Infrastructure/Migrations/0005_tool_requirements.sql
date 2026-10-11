-- V3.9: tooling as a readiness dimension (V3 brief B).
--
-- An activity (on one machine, or on any machine) can need tools of a type: a slitter run needs a knife set, an
-- inspection needs a calibrated caliper. Readiness then asks whether enough tools of that type are valid for that
-- machine and activity at that moment (calibration in force, not out for service, not damaged, within limits).
-- Removing a requirement keeps its audit record; the row itself goes.

CREATE TABLE resources.tool_requirement (
    requirement_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    activity_code    text NOT NULL,
    machine_code     text NULL,
    tool_type_code   text NOT NULL REFERENCES resources.tool_type(code),
    quantity         integer NOT NULL DEFAULT 1 CHECK (quantity BETWEEN 1 AND 50),
    set_at           timestamptz NOT NULL,
    set_by           text NOT NULL
);
CREATE UNIQUE INDEX tool_requirement_key ON resources.tool_requirement (activity_code, coalesce(machine_code, ''), tool_type_code);
