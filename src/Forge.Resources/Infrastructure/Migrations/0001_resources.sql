CREATE SCHEMA IF NOT EXISTS resources;

CREATE TABLE resources.site (
    code        text PRIMARY KEY,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL,
    created_by  text NOT NULL
);

CREATE TABLE resources.safety_area (
    code        text PRIMARY KEY,
    site_code   text NOT NULL REFERENCES resources.site(code),
    name        text NOT NULL,
    created_at  timestamptz NOT NULL,
    created_by  text NOT NULL
);

CREATE TABLE resources.work_center (
    code              text PRIMARY KEY,
    safety_area_code  text NOT NULL REFERENCES resources.safety_area(code),
    name              text NOT NULL,
    created_at        timestamptz NOT NULL,
    created_by        text NOT NULL
);

CREATE TABLE resources.machine (
    code              text PRIMARY KEY,
    work_center_code  text NOT NULL REFERENCES resources.work_center(code),
    name              text NOT NULL,
    created_at        timestamptz NOT NULL,
    created_by        text NOT NULL
);

CREATE TABLE resources.activity_type (
    code         text PRIMARY KEY,
    name         text NOT NULL,
    description  text NULL,
    created_at   timestamptz NOT NULL,
    created_by   text NOT NULL
);

-- Machine requirement profile: which activities run on a machine and which qualifications an operator needs.
CREATE TABLE resources.machine_activity (
    machine_code             text NOT NULL REFERENCES resources.machine(code),
    activity_code            text NOT NULL REFERENCES resources.activity_type(code),
    operator_qualifications  text[] NOT NULL CHECK (cardinality(operator_qualifications) > 0),
    updated_at               timestamptz NOT NULL,
    updated_by               text NOT NULL,
    PRIMARY KEY (machine_code, activity_code)
);

-- Activities performed at a work center without a machine (staging, kitting, cleaning).
CREATE TABLE resources.work_center_activity (
    work_center_code  text NOT NULL REFERENCES resources.work_center(code),
    activity_code     text NOT NULL REFERENCES resources.activity_type(code),
    created_at        timestamptz NOT NULL,
    created_by        text NOT NULL,
    PRIMARY KEY (work_center_code, activity_code)
);
