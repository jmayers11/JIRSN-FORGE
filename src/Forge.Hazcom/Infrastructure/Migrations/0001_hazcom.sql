-- Owner decisions 2026-11-01: hazard communication (OSHA 29 CFR 1910.1200). The chemical register, and safety data sheets the EHS
-- Coordinator approves; one approved SDS is in force per material, older ones are superseded and kept.
CREATE SCHEMA IF NOT EXISTS hazcom;

CREATE TABLE hazcom.chemical (
    part_number        text PRIMARY KEY REFERENCES engineering.item(part_number),
    hazardous          boolean NOT NULL,
    signal_word        text NOT NULL CHECK (signal_word IN ('None', 'Warning', 'Danger')),
    pictograms         text[] NOT NULL DEFAULT '{}',
    hazard_statements  text NULL,
    ppe                text NULL,
    storage            text NULL,
    updated_by         text NOT NULL,
    updated_at         timestamptz NOT NULL,
    -- Owner decision 2026-11-01: a newly hazardous chemical waits for the EHS Coordinator to decide whether HazCom training covers it.
    training_review_due   boolean NOT NULL DEFAULT false,
    training_reviewed_by  text NULL,
    training_reviewed_at  timestamptz NULL,
    training_review_note  text NULL
);

CREATE TABLE hazcom.sds (
    sds_id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number        text NOT NULL REFERENCES engineering.item(part_number),
    manufacturer       text NOT NULL,
    product_name       text NOT NULL,
    revision_date      date NOT NULL,
    file_sha256        text NOT NULL CHECK (length(file_sha256) = 64),
    file_name          text NULL,
    status             text NOT NULL CHECK (status IN ('Submitted', 'Approved', 'Rejected', 'Superseded')),
    submitted_by       text NOT NULL,
    submitted_at       timestamptz NOT NULL,
    decided_by         text NULL,
    decided_at         timestamptz NULL,
    decision_note      text NULL,
    review_due         date NULL,
    reviewed_by        text NULL,
    reviewed_at        timestamptz NULL,
    flagged_for        date NULL,
    CHECK ((status = 'Approved') <= (review_due IS NOT NULL))
);
CREATE UNIQUE INDEX sds_one_approved ON hazcom.sds (part_number) WHERE status = 'Approved';
