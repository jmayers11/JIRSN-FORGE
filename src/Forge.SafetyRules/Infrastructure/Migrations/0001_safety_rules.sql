CREATE SCHEMA IF NOT EXISTS safety;

CREATE TABLE safety.rule (
    code                     text NOT NULL,
    version                  integer NOT NULL CHECK (version >= 1),
    title                    text NOT NULL,
    scope_kind               text NOT NULL CHECK (scope_kind IN ('SafetyArea', 'WorkCenter', 'Machine')),
    scope_code               text NOT NULL,
    activity_codes           text[] NOT NULL,
    minimum_physical_people  integer NOT NULL CHECK (minimum_physical_people BETWEEN 1 AND 50),
    prohibit_lone_work       boolean NOT NULL,
    status                   text NOT NULL CHECK (status IN ('Draft', 'Published', 'Retired')),
    authored_by              text NOT NULL,
    authored_at              timestamptz NOT NULL,
    approved_by              text NULL,
    effective_from           timestamptz NULL,
    effective_to             timestamptz NULL,
    PRIMARY KEY (code, version),
    CHECK (status = 'Draft' OR (approved_by IS NOT NULL AND effective_from IS NOT NULL)),
    CHECK (approved_by IS NULL OR approved_by <> authored_by)
);

-- At most one draft and one version in force per rule.
CREATE UNIQUE INDEX rule_one_draft ON safety.rule (code) WHERE status = 'Draft';
CREATE UNIQUE INDEX rule_one_published ON safety.rule (code) WHERE status = 'Published';

CREATE TABLE safety.rule_slot (
    code              text NOT NULL,
    version           integer NOT NULL,
    ordinal           integer NOT NULL,
    name              text NOT NULL,
    required_count    integer NOT NULL CHECK (required_count BETWEEN 1 AND 20),
    satisfied_by      text[] NOT NULL CHECK (cardinality(satisfied_by) > 0),
    trainee_may_fill  boolean NOT NULL,
    PRIMARY KEY (code, version, ordinal),
    FOREIGN KEY (code, version) REFERENCES safety.rule(code, version) ON DELETE CASCADE
);
