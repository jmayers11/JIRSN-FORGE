ALTER TABLE workforce.attendance_intelligence_event
    ADD COLUMN source_record_id text NULL;

ALTER TABLE workforce.attendance_intelligence_event
    ADD CONSTRAINT attendance_external_source_record_not_blank
    CHECK (source_record_id IS NULL OR length(btrim(source_record_id)) > 0);

CREATE UNIQUE INDEX attendance_external_source_record
    ON workforce.attendance_intelligence_event(source, source_record_id)
    WHERE source_record_id IS NOT NULL;
