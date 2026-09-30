CREATE SCHEMA IF NOT EXISTS competency;

CREATE TABLE competency.qualification_type (
    code           text PRIMARY KEY,
    name           text NOT NULL,
    kind           text NOT NULL CHECK (kind IN ('General', 'Process', 'Machine', 'SpecialProcess', 'Safety', 'Trainer')),
    validity_days  integer NULL CHECK (validity_days > 0),
    created_at     timestamptz NOT NULL,
    created_by     text NOT NULL
);

CREATE TABLE competency.prerequisite (
    code               text NOT NULL REFERENCES competency.qualification_type(code),
    prerequisite_code  text NOT NULL REFERENCES competency.qualification_type(code),
    PRIMARY KEY (code, prerequisite_code),
    CHECK (code <> prerequisite_code)
);

-- Current state per person and qualification. History lives in the audit log and event log.
CREATE TABLE competency.person_qualification (
    person_id     uuid NOT NULL REFERENCES kernel.person(person_id),
    code          text NOT NULL REFERENCES competency.qualification_type(code),
    status        text NOT NULL CHECK (status IN ('InTraining', 'Qualified', 'Suspended', 'Revoked')),
    certified_at  timestamptz NULL,
    expires_at    timestamptz NULL,
    evidence      text NULL,
    updated_at    timestamptz NOT NULL,
    updated_by    text NOT NULL,
    PRIMARY KEY (person_id, code),
    CHECK (status <> 'Qualified' OR certified_at IS NOT NULL)
);
