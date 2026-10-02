-- Saved report definitions: a standard report with its filters and grouping, private to the owner or shared.
CREATE TABLE planning.report_definition (
    definition_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name            text NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    report_id       text NOT NULL,
    parameters      text NOT NULL,
    shared          boolean NOT NULL,
    owner           text NOT NULL,
    created_at      timestamptz NOT NULL,
    updated_at      timestamptz NOT NULL,
    deleted_at      timestamptz NULL
);
CREATE UNIQUE INDEX report_definition_name ON planning.report_definition (owner, lower(name)) WHERE deleted_at IS NULL;
