-- V2.5: propagation of a released engineering change, its verification record, and the actions it raises.

-- One verification record per released change: what propagation expected to do and what it did.
-- The change stays "Released — propagation incomplete" until every step is done.
CREATE TABLE engineering.change_propagation (
    change_number   text PRIMARY KEY REFERENCES engineering.change(change_number),
    status          text NOT NULL CHECK (status IN ('Running', 'Complete', 'Incomplete')),
    correlation_id  uuid NOT NULL,
    runs            integer NOT NULL CHECK (runs >= 0),
    started_at      timestamptz NOT NULL,
    last_run_at     timestamptz NULL,
    completed_at    timestamptz NULL,
    last_error      text NULL,
    CHECK ((status = 'Complete') = (completed_at IS NOT NULL))
);

-- Each record propagation touches, one step each. A step is done once and never undone; a failed step
-- keeps its error and is tried again; a waiting step waits for the change's effective date.
CREATE TABLE engineering.propagation_step (
    change_number  text NOT NULL REFERENCES engineering.change_propagation(change_number),
    step_key       text NOT NULL,
    kind           text NOT NULL CHECK (kind IN ('MoveOrder', 'ReissueDocumentation', 'WipDisposition', 'PurchaseReview', 'TrainingAcknowledgment', 'CompletedUnchanged')),
    target         text NOT NULL,
    part_number    text NULL,
    detail         text NOT NULL,
    status         text NOT NULL CHECK (status IN ('Pending', 'Done', 'Failed', 'Waiting')),
    attempts       integer NOT NULL DEFAULT 0,
    error          text NULL,
    planned_at     timestamptz NOT NULL,
    done_at        timestamptz NULL,
    PRIMARY KEY (change_number, step_key),
    CHECK ((status = 'Done') = (done_at IS NOT NULL))
);

-- What a person decides because of a change: a disposition for work in progress, a review of an open
-- purchase order line, a training acknowledgment of a new work instruction. Raised once per target.
CREATE TABLE engineering.change_action (
    action_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    change_number    text NOT NULL REFERENCES engineering.change(change_number),
    kind             text NOT NULL CHECK (kind IN ('WipDisposition', 'PurchaseReview', 'TrainingAcknowledgment')),
    target           text NOT NULL,
    part_number      text NULL,
    document_number  text NULL,
    revision         text NULL,
    person_id        uuid NULL,
    program_id       uuid NULL,
    detail           text NOT NULL,
    status           text NOT NULL CHECK (status IN ('Open', 'Resolved')),
    resolution       text NULL,
    notes            text NULL,
    raised_by        text NOT NULL,
    raised_at        timestamptz NOT NULL,
    resolved_by      text NULL,
    resolved_at      timestamptz NULL,
    UNIQUE (change_number, kind, target),
    CHECK ((status = 'Resolved') = (resolved_at IS NOT NULL) AND (resolved_at IS NULL) = (resolved_by IS NULL) AND (resolved_at IS NULL) = (resolution IS NULL)),
    CHECK (kind <> 'TrainingAcknowledgment' OR person_id IS NOT NULL)
);
CREATE INDEX change_action_open ON engineering.change_action (status, kind);

CREATE TRIGGER change_propagation_kept BEFORE DELETE ON engineering.change_propagation FOR EACH ROW EXECUTE FUNCTION engineering.reject_change_delete();
CREATE TRIGGER propagation_step_kept BEFORE DELETE ON engineering.propagation_step FOR EACH ROW EXECUTE FUNCTION engineering.reject_change_delete();
CREATE TRIGGER change_action_kept BEFORE DELETE ON engineering.change_action FOR EACH ROW EXECUTE FUNCTION engineering.reject_change_delete();

-- A done step and a resolved action never change again.
CREATE FUNCTION engineering.freeze_propagation_step() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status = 'Done' THEN
        RAISE EXCEPTION 'propagation step % of % is done and frozen', OLD.step_key, OLD.change_number USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER propagation_step_frozen BEFORE UPDATE ON engineering.propagation_step FOR EACH ROW EXECUTE FUNCTION engineering.freeze_propagation_step();

CREATE FUNCTION engineering.freeze_change_action() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status = 'Resolved' THEN
        RAISE EXCEPTION 'action % is resolved and frozen', OLD.action_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.change_number <> OLD.change_number OR NEW.kind <> OLD.kind OR NEW.target <> OLD.target OR NEW.raised_at <> OLD.raised_at THEN
        RAISE EXCEPTION 'action % keeps what it was raised for', OLD.action_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER change_action_frozen BEFORE UPDATE ON engineering.change_action FOR EACH ROW EXECUTE FUNCTION engineering.freeze_change_action();
