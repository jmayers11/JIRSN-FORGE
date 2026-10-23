ALTER TABLE inventory.material_task
    ADD COLUMN source_suspect_report_id bigint NULL REFERENCES quality.suspect_scrap_report(report_id);

CREATE UNIQUE INDEX material_task_one_replacement_per_suspect_report
    ON inventory.material_task(source_suspect_report_id)
    WHERE source_suspect_report_id IS NOT NULL;
