CREATE TABLE competency.prerequisite_alternative_group (
    code        text NOT NULL REFERENCES competency.qualification_type(code),
    group_code  text NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL,
    created_by  text NOT NULL,
    PRIMARY KEY (code, group_code),
    CHECK (length(btrim(name)) > 0)
);

CREATE TABLE competency.prerequisite_alternative_member (
    code               text NOT NULL,
    group_code         text NOT NULL,
    prerequisite_code text NOT NULL REFERENCES competency.qualification_type(code),
    PRIMARY KEY (code, group_code, prerequisite_code),
    FOREIGN KEY (code, group_code)
        REFERENCES competency.prerequisite_alternative_group(code, group_code),
    CHECK (code <> prerequisite_code)
);

CREATE INDEX prerequisite_alternative_member_lookup
    ON competency.prerequisite_alternative_member (prerequisite_code, code);
