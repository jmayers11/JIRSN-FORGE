ALTER TABLE inventory.material_task
    ADD COLUMN operation_sequence integer NULL,
    ADD COLUMN requester_person_id uuid NULL REFERENCES kernel.person(person_id),
    ADD CONSTRAINT material_task_operation_context CHECK (operation_sequence IS NULL OR order_number IS NOT NULL),
    ADD CONSTRAINT material_task_operation_fk FOREIGN KEY (order_number, operation_sequence)
        REFERENCES execution.order_operation(order_number, sequence);

CREATE INDEX material_task_operation ON inventory.material_task(order_number, operation_sequence)
    WHERE order_number IS NOT NULL AND operation_sequence IS NOT NULL;
