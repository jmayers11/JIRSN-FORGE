-- Plant setup: records can be renamed, moved and retired (never deleted, so history keeps its meaning).
ALTER TABLE resources.site
    ADD COLUMN updated_at timestamptz NULL,
    ADD COLUMN updated_by text NULL;

ALTER TABLE resources.safety_area
    ADD COLUMN active boolean NOT NULL DEFAULT true,
    ADD COLUMN updated_at timestamptz NULL,
    ADD COLUMN updated_by text NULL;

ALTER TABLE resources.work_center
    ADD COLUMN active boolean NOT NULL DEFAULT true,
    ADD COLUMN picture text NULL,
    ADD COLUMN updated_at timestamptz NULL,
    ADD COLUMN updated_by text NULL;

ALTER TABLE resources.machine
    ADD COLUMN active boolean NOT NULL DEFAULT true,
    ADD COLUMN updated_at timestamptz NULL,
    ADD COLUMN updated_by text NULL;

ALTER TABLE resources.activity_type
    ADD COLUMN active boolean NOT NULL DEFAULT true,
    ADD COLUMN updated_at timestamptz NULL,
    ADD COLUMN updated_by text NULL;
