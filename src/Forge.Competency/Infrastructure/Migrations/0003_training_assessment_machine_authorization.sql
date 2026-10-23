CREATE TABLE competency.training_course_completion (
    completion_id uuid PRIMARY KEY,
    person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    course_code text NOT NULL REFERENCES competency.qualification_type(code),
    course_version text NOT NULL,
    trainer_person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    completed_at timestamptz NOT NULL,
    expires_at timestamptz NULL,
    evidence text NOT NULL,
    CHECK (person_id <> trainer_person_id),
    CHECK (length(btrim(course_version)) > 0),
    CHECK (length(btrim(evidence)) > 0)
);
CREATE INDEX training_course_completion_person_code ON competency.training_course_completion(person_id, course_code, completed_at DESC);

CREATE TABLE competency.practical_assessment (
    assessment_id uuid PRIMARY KEY,
    person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    qualification_code text NOT NULL REFERENCES competency.qualification_type(code),
    machine_code text NOT NULL REFERENCES resources.machine(code),
    activity_code text NOT NULL REFERENCES resources.activity_type(code),
    assessor_person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    passed boolean NOT NULL,
    assessment_version text NOT NULL,
    assessed_at timestamptz NOT NULL,
    evidence text NOT NULL,
    restrictions text NULL,
    CHECK (person_id <> assessor_person_id),
    CHECK (length(btrim(assessment_version)) > 0),
    CHECK (length(btrim(evidence)) > 0)
);
CREATE INDEX practical_assessment_person_machine ON competency.practical_assessment(person_id, machine_code, activity_code, qualification_code, assessed_at DESC);

CREATE TABLE competency.machine_authorization (
    person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    machine_code text NOT NULL REFERENCES resources.machine(code),
    activity_code text NOT NULL REFERENCES resources.activity_type(code),
    qualification_code text NOT NULL REFERENCES competency.qualification_type(code),
    status text NOT NULL CHECK (status IN ('Authorized', 'Suspended', 'Revoked')),
    independent_operation_allowed boolean NOT NULL DEFAULT false,
    authorized_at timestamptz NOT NULL,
    expires_at timestamptz NULL,
    revision integer NOT NULL CHECK (revision > 0),
    course_completion_id uuid NOT NULL REFERENCES competency.training_course_completion(completion_id),
    assessment_id uuid NOT NULL REFERENCES competency.practical_assessment(assessment_id),
    evidence text NOT NULL,
    restrictions text NULL,
    approved_by uuid NOT NULL REFERENCES kernel.person(person_id),
    updated_at timestamptz NOT NULL,
    updated_by text NOT NULL,
    PRIMARY KEY (person_id, machine_code, activity_code, qualification_code),
    CHECK (length(btrim(evidence)) > 0)
);

CREATE TABLE competency.machine_authorization_event (
    event_id bigserial PRIMARY KEY,
    person_id uuid NOT NULL,
    machine_code text NOT NULL,
    activity_code text NOT NULL,
    qualification_code text NOT NULL,
    status text NOT NULL CHECK (status IN ('Authorized', 'Suspended', 'Revoked')),
    independent_operation_allowed boolean NOT NULL DEFAULT false,
    revision integer NOT NULL,
    course_completion_id uuid NOT NULL,
    assessment_id uuid NOT NULL,
    reason text NOT NULL,
    recorded_at timestamptz NOT NULL,
    recorded_by text NOT NULL
);
CREATE INDEX machine_authorization_event_key ON competency.machine_authorization_event(person_id, machine_code, activity_code, qualification_code, event_id);
