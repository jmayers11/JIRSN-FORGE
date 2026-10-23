ALTER TABLE inventory.material_task DROP CONSTRAINT material_task_status_check;
ALTER TABLE inventory.material_task ADD CONSTRAINT material_task_status_check
    CHECK (status IN ('Requested', 'Allocated', 'Picked', 'InTransit', 'Delivered', 'Cancelled'));

ALTER TABLE inventory.material_task
    ADD COLUMN transit_location text NULL REFERENCES inventory.location(code),
    ADD COLUMN receiver_person_id uuid NULL REFERENCES kernel.person(person_id),
    ADD COLUMN received_by text NULL,
    ADD COLUMN received_at timestamptz NULL,
    ADD COLUMN receipt_transaction_id bigint NULL REFERENCES inventory.transaction(transaction_id),
    ADD CONSTRAINT material_task_in_transit_details CHECK (
        status <> 'InTransit' OR (transit_location IS NOT NULL AND delivered_by IS NOT NULL AND delivered_at IS NOT NULL AND transaction_id IS NOT NULL)
    );

DROP INDEX inventory.material_task_open;
CREATE INDEX material_task_open ON inventory.material_task (status, part_number) WHERE status IN ('Requested', 'Allocated', 'Picked', 'InTransit');

CREATE TABLE inventory.material_task_custody_event (
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    task_id bigint NOT NULL REFERENCES inventory.material_task(task_id),
    kind text NOT NULL CHECK (kind IN ('DispatchedToTransit', 'ReceivedAtDestination', 'ExceptionRecorded')),
    recorded_at timestamptz NOT NULL,
    recorded_by text NOT NULL,
    person_id uuid NULL REFERENCES kernel.person(person_id),
    location_code text NOT NULL REFERENCES inventory.location(code),
    transaction_id bigint NULL REFERENCES inventory.transaction(transaction_id),
    authentication_method text NULL,
    reason text NULL
);
CREATE INDEX material_task_custody_history ON inventory.material_task_custody_event(task_id, event_id);
