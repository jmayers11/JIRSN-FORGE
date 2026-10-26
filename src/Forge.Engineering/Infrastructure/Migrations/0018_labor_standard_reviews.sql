-- Sprint 10 (WW-S10-10): a suspect labor standard is marked for review and not used to judge efficiency until an engineer decides it.
-- Reviews are raised and decided once; the latest review of an operation decides how its standard is used.
CREATE TABLE engineering.labor_standard_review (
    review_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number  text NOT NULL,
    revision     text NOT NULL,
    sequence     integer NOT NULL,
    status       text NOT NULL CHECK (status IN ('Open', 'Confirmed', 'Corrected')),
    reason       text NOT NULL,
    raised_by    text NOT NULL,
    raised_at    timestamptz NOT NULL,
    decided_by   text NULL,
    decided_at   timestamptz NULL,
    resolution   text NULL,
    FOREIGN KEY (part_number, revision) REFERENCES engineering.revision(part_number, revision),
    CHECK ((status = 'Open') = (decided_at IS NULL)),
    CHECK (status = 'Open' OR (decided_by IS NOT NULL AND resolution IS NOT NULL))
);

CREATE UNIQUE INDEX labor_standard_review_one_open ON engineering.labor_standard_review (part_number, revision, sequence) WHERE status = 'Open';
