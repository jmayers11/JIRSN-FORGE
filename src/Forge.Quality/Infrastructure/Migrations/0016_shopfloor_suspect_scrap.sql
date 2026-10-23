CREATE TABLE quality.suspect_scrap_report (
    report_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind               text NOT NULL CHECK (kind IN ('Scrap', 'QualityIssue')),
    order_number       bigint NOT NULL REFERENCES execution.production_order(order_number),
    sequence           integer NOT NULL,
    part_number        text NOT NULL REFERENCES engineering.item(part_number),
    revision           text NULL,
    lot_number         text NULL,
    serial_number      text NULL,
    quantity           numeric(18, 6) NOT NULL CHECK (quantity > 0),
    machine_code       text NULL,
    reporter_person_id uuid NOT NULL REFERENCES workforce.worker(person_id),
    reason_code        text NOT NULL,
    description        text NOT NULL,
    reported_at        timestamptz NOT NULL,
    ncr_id             bigint NULL REFERENCES quality.ncr(ncr_id),
    FOREIGN KEY (order_number, sequence) REFERENCES execution.order_operation(order_number, sequence)
);

CREATE INDEX suspect_scrap_report_order_time ON quality.suspect_scrap_report(order_number, reported_at DESC);
CREATE INDEX suspect_scrap_report_review_queue ON quality.suspect_scrap_report(reported_at DESC) WHERE ncr_id IS NULL;
