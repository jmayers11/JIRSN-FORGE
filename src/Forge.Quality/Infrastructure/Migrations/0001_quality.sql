CREATE SCHEMA IF NOT EXISTS quality;

CREATE TABLE quality.gauge (
    code             text PRIMARY KEY,
    description      text NOT NULL,
    calibration_due  date NOT NULL,
    certificate      text NULL,
    active           boolean NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL,
    created_by       text NOT NULL
);

CREATE TABLE quality.calibration (
    calibration_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    gauge_code      text NOT NULL REFERENCES quality.gauge(code),
    calibrated_on   date NOT NULL,
    due_date        date NOT NULL CHECK (due_date > calibrated_on),
    certificate     text NULL,
    recorded_at     timestamptz NOT NULL,
    recorded_by     text NOT NULL
);

-- An inspection point after an operation of a part. Plans can change; inspections keep the limits they used.
CREATE TABLE quality.inspection_plan (
    part_number  text NOT NULL REFERENCES engineering.item(part_number),
    sequence     integer NOT NULL CHECK (sequence BETWEEN 1 AND 9999),
    title        text NOT NULL,
    updated_at   timestamptz NOT NULL,
    updated_by   text NOT NULL,
    PRIMARY KEY (part_number, sequence)
);

CREATE TABLE quality.characteristic (
    part_number  text NOT NULL,
    sequence     integer NOT NULL,
    number       integer NOT NULL CHECK (number BETWEEN 1 AND 999),
    name         text NOT NULL,
    kind         text NOT NULL CHECK (kind IN ('Variable', 'Attribute')),
    nominal      numeric(18, 6) NULL,
    lower_limit  numeric(18, 6) NULL,
    upper_limit  numeric(18, 6) NULL,
    unit         text NULL,
    PRIMARY KEY (part_number, sequence, number),
    FOREIGN KEY (part_number, sequence) REFERENCES quality.inspection_plan(part_number, sequence) ON DELETE CASCADE,
    CHECK (kind = 'Attribute' OR lower_limit IS NOT NULL OR upper_limit IS NOT NULL),
    CHECK (lower_limit IS NULL OR upper_limit IS NULL OR lower_limit <= upper_limit)
);

CREATE TABLE quality.ncr (
    ncr_id             bigint GENERATED ALWAYS AS IDENTITY (START WITH 9001) PRIMARY KEY,
    part_number        text NOT NULL REFERENCES engineering.item(part_number),
    order_number       bigint NULL REFERENCES execution.production_order(order_number),
    sequence           integer NULL,
    lot_number         text NULL,
    quantity           numeric(18, 6) NOT NULL CHECK (quantity > 0),
    defect             text NOT NULL,
    description        text NOT NULL,
    status             text NOT NULL CHECK (status IN ('Open', 'AwaitingApproval', 'Dispositioned', 'Closed')),
    disposition        text NULL CHECK (disposition IN ('UseAsIs', 'Rework', 'Scrap', 'ReturnToSupplier')),
    disposition_note   text NULL,
    raised_by          text NOT NULL,
    raised_at          timestamptz NOT NULL,
    dispositioned_by   text NULL,
    dispositioned_at   timestamptz NULL,
    approved_by        text NULL,
    approved_at        timestamptz NULL,
    closed_by          text NULL,
    closed_at          timestamptz NULL,
    closure_note       text NULL,
    -- Use-as-is accepts nonconforming material: a second person must approve it.
    CHECK (approved_by IS NULL OR approved_by <> dispositioned_by)
);

CREATE TABLE quality.inspection (
    inspection_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number        bigint NOT NULL REFERENCES execution.production_order(order_number),
    sequence            integer NOT NULL,
    part_number         text NOT NULL,
    quantity_inspected  numeric(18, 6) NOT NULL CHECK (quantity_inspected > 0),
    result              text NOT NULL CHECK (result IN ('Pass', 'Fail')),
    gauge_code          text NULL REFERENCES quality.gauge(code),
    notes               text NULL,
    inspector           text NOT NULL,
    at                  timestamptz NOT NULL,
    ncr_id              bigint NULL REFERENCES quality.ncr(ncr_id)
);
CREATE INDEX inspection_order ON quality.inspection (order_number, sequence);

CREATE TABLE quality.inspection_result (
    inspection_id  bigint NOT NULL REFERENCES quality.inspection(inspection_id),
    number         integer NOT NULL,
    name           text NOT NULL,
    kind           text NOT NULL,
    lower_limit    numeric(18, 6) NULL,
    upper_limit    numeric(18, 6) NULL,
    measured       numeric(18, 6) NULL,
    accepted       boolean NULL,
    pass           boolean NOT NULL,
    PRIMARY KEY (inspection_id, number)
);

-- Inspection records are evidence: they are never changed.
CREATE FUNCTION quality.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'inspection records cannot be changed or deleted' USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER inspection_immutable BEFORE UPDATE OR DELETE ON quality.inspection FOR EACH ROW EXECUTE FUNCTION quality.reject_change();
CREATE TRIGGER inspection_result_immutable BEFORE UPDATE OR DELETE ON quality.inspection_result FOR EACH ROW EXECUTE FUNCTION quality.reject_change();

CREATE TABLE quality.capa (
    capa_id              bigint GENERATED ALWAYS AS IDENTITY (START WITH 501) PRIMARY KEY,
    ncr_id               bigint NULL REFERENCES quality.ncr(ncr_id),
    title                text NOT NULL,
    root_cause           text NOT NULL,
    action               text NOT NULL,
    due_date             date NOT NULL,
    status               text NOT NULL CHECK (status IN ('Open', 'Implemented', 'Verified')),
    opened_by            text NOT NULL,
    opened_at            timestamptz NOT NULL,
    implemented_by       text NULL,
    implemented_at       timestamptz NULL,
    implementation_note  text NULL,
    verified_by          text NULL,
    verified_at          timestamptz NULL,
    CHECK (verified_by IS NULL OR verified_by <> implemented_by)
);
