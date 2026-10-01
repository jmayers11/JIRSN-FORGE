CREATE SCHEMA IF NOT EXISTS execution;

CREATE TABLE execution.production_order (
    order_number  bigint GENERATED ALWAYS AS IDENTITY (START WITH 10001) PRIMARY KEY,
    part_number   text NOT NULL,
    revision      text NOT NULL,
    quantity      numeric(18, 6) NOT NULL CHECK (quantity > 0),
    due_date      date NOT NULL,
    priority      integer NOT NULL CHECK (priority BETWEEN 1 AND 9),
    status        text NOT NULL CHECK (status IN ('Planned', 'FirmPlanned', 'Released', 'Held', 'Cancelled')),
    held_from     text NULL,
    program_id    uuid NULL,
    created_at    timestamptz NOT NULL,
    created_by    text NOT NULL,
    released_at   timestamptz NULL,
    updated_at    timestamptz NOT NULL,
    updated_by    text NOT NULL,
    FOREIGN KEY (part_number, revision) REFERENCES engineering.revision(part_number, revision),
    CHECK ((status = 'Held') = (held_from IS NOT NULL))
);
CREATE INDEX production_order_program ON execution.production_order (program_id);

-- Copied from the routing at release. Later routing changes do not touch these rows.
CREATE TABLE execution.order_operation (
    order_number             bigint NOT NULL REFERENCES execution.production_order(order_number),
    sequence                 integer NOT NULL,
    description              text NOT NULL,
    work_center_code         text NOT NULL,
    activity_code            text NOT NULL,
    eligible_machines        text[] NOT NULL,
    required_qualifications  text[] NOT NULL,
    planned_minutes          numeric(14, 4) NOT NULL CHECK (planned_minutes >= 0),
    crew_size                integer NOT NULL,
    status                   text NOT NULL CHECK (status IN ('Pending', 'InProgress', 'Complete')),
    PRIMARY KEY (order_number, sequence)
);

-- Copied from the BOM at release, with the component revision in force at that moment.
CREATE TABLE execution.order_material (
    order_number        bigint NOT NULL REFERENCES execution.production_order(order_number),
    line_number         integer NOT NULL,
    component           text NOT NULL,
    component_revision  text NULL,
    required_quantity   numeric(18, 6) NOT NULL,
    unit_of_measure     text NOT NULL,
    PRIMARY KEY (order_number, line_number)
);
