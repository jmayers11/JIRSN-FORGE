-- V2.10: facility and capital projects. Approved by a second person; after approval every change to what
-- the project is (budget, dates, financial reference, material) is a change request approved by someone else.
CREATE SCHEMA IF NOT EXISTS projects;

CREATE TABLE projects.project (
    code                text PRIMARY KEY,
    title               text NOT NULL,
    type                text NOT NULL CHECK (type IN ('Facility', 'Capital')),
    funding             text NOT NULL CHECK (funding IN ('Operating', 'Capital')),
    description         text NULL,
    owner               text NULL,
    budget              numeric(18, 2) NULL CHECK (budget IS NULL OR budget >= 0),
    start_date          date NOT NULL,
    end_date            date NOT NULL,
    financial_reference text NULL,
    status              text NOT NULL CHECK (status IN ('Draft', 'Approved', 'Completed', 'Cancelled')),
    created_by          text NOT NULL,
    created_at          timestamptz NOT NULL,
    approved_by         text NULL,
    approved_at         timestamptz NULL,
    closed_by           text NULL,
    closed_at           timestamptz NULL,
    close_reason        text NULL,
    CHECK (end_date >= start_date),
    CHECK ((status = 'Draft') = (approved_by IS NULL) OR status = 'Cancelled'),
    CHECK (approved_by IS NULL OR approved_by <> created_by),
    CHECK (funding <> 'Capital' OR status = 'Draft' OR status = 'Cancelled' OR financial_reference IS NOT NULL),
    CHECK ((status IN ('Completed', 'Cancelled')) = (closed_at IS NOT NULL))
);

CREATE TABLE projects.line (
    project_code text NOT NULL REFERENCES projects.project(code),
    line_number  integer NOT NULL CHECK (line_number > 0),
    part_number  text NOT NULL REFERENCES engineering.item(part_number),
    quantity     numeric(18, 6) NOT NULL CHECK (quantity > 0),
    need_date    date NOT NULL,
    description  text NULL,
    status       text NOT NULL CHECK (status IN ('Active', 'Cancelled')),
    PRIMARY KEY (project_code, line_number)
);

CREATE TABLE projects.task (
    task_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_code text NOT NULL REFERENCES projects.project(code),
    title        text NOT NULL,
    due_date     date NOT NULL,
    milestone    boolean NOT NULL,
    completed_on date NULL,
    completed_by text NULL,
    created_by   text NOT NULL,
    created_at   timestamptz NOT NULL,
    CHECK ((completed_on IS NULL) = (completed_by IS NULL))
);

CREATE TABLE projects.change (
    change_id          bigint GENERATED ALWAYS AS IDENTITY (START WITH 501) PRIMARY KEY,
    project_code       text NOT NULL REFERENCES projects.project(code),
    kind               text NOT NULL CHECK (kind IN ('Header', 'AddLine', 'ChangeLine', 'CancelLine')),
    line_number        integer NULL,
    part_number        text NULL,
    quantity           numeric(18, 6) NULL,
    need_date          date NULL,
    line_description   text NULL,
    title              text NULL,
    description        text NULL,
    budget             numeric(18, 2) NULL,
    start_date         date NULL,
    end_date           date NULL,
    financial_reference text NULL,
    reason             text NOT NULL CHECK (length(trim(reason)) > 0),
    status             text NOT NULL CHECK (status IN ('Pending', 'Approved', 'Rejected')),
    requested_by       text NOT NULL,
    requested_at       timestamptz NOT NULL,
    decided_by         text NULL,
    decided_at         timestamptz NULL,
    decision_reason    text NULL,
    CHECK ((status = 'Pending') = (decided_at IS NULL)),
    CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CHECK ((kind = 'Header') = (line_number IS NULL))
);
CREATE INDEX change_pending ON projects.change (project_code) WHERE status = 'Pending';

-- Nothing is deleted; a decided change request is history and never edited.
CREATE FUNCTION projects.reject_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'project records are never deleted' USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER project_kept BEFORE DELETE ON projects.project FOR EACH ROW EXECUTE FUNCTION projects.reject_delete();
CREATE TRIGGER line_kept BEFORE DELETE ON projects.line FOR EACH ROW EXECUTE FUNCTION projects.reject_delete();
CREATE TRIGGER task_kept BEFORE DELETE ON projects.task FOR EACH ROW EXECUTE FUNCTION projects.reject_delete();
CREATE TRIGGER change_kept BEFORE DELETE ON projects.change FOR EACH ROW EXECUTE FUNCTION projects.reject_delete();

CREATE FUNCTION projects.freeze_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status <> 'Pending' THEN
        RAISE EXCEPTION 'project change % was decided and is history', OLD.change_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF (NEW.project_code, NEW.kind, NEW.line_number, NEW.part_number, NEW.quantity, NEW.need_date, NEW.line_description, NEW.title, NEW.description,
        NEW.budget, NEW.start_date, NEW.end_date, NEW.financial_reference, NEW.reason, NEW.requested_by, NEW.requested_at)
       IS DISTINCT FROM (OLD.project_code, OLD.kind, OLD.line_number, OLD.part_number, OLD.quantity, OLD.need_date, OLD.line_description, OLD.title, OLD.description,
        OLD.budget, OLD.start_date, OLD.end_date, OLD.financial_reference, OLD.reason, OLD.requested_by, OLD.requested_at) THEN
        RAISE EXCEPTION 'project change % keeps what was asked and why', OLD.change_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER change_frozen BEFORE UPDATE ON projects.change FOR EACH ROW EXECUTE FUNCTION projects.freeze_change();

-- A closed project stays as it was closed.
CREATE FUNCTION projects.freeze_closed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status IN ('Completed', 'Cancelled') THEN
        RAISE EXCEPTION 'project % is %', OLD.code, OLD.status USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER project_closed_frozen BEFORE UPDATE ON projects.project FOR EACH ROW EXECUTE FUNCTION projects.freeze_closed();
