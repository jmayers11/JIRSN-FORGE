ALTER TABLE execution.shift_handoff_action
    ADD COLUMN due_at timestamptz NULL,
    ADD COLUMN completion_evidence text NULL;

CREATE TABLE execution.shift_handoff_action_dependency (
    action_id bigint NOT NULL REFERENCES execution.shift_handoff_action(action_id) ON DELETE CASCADE,
    blocked_by_action_id bigint NOT NULL REFERENCES execution.shift_handoff_action(action_id),
    PRIMARY KEY (action_id, blocked_by_action_id),
    CHECK (action_id <> blocked_by_action_id)
);

CREATE INDEX shift_handoff_action_dependency_blocker
    ON execution.shift_handoff_action_dependency (blocked_by_action_id, action_id);
