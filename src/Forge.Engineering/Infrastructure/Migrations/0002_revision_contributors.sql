-- Separation of duties: nobody who created or edited a revision may release it.
ALTER TABLE engineering.revision ADD COLUMN contributors text[] NOT NULL DEFAULT '{}';
UPDATE engineering.revision SET contributors = ARRAY[created_by];
ALTER TABLE engineering.revision ADD CONSTRAINT revision_releaser_not_contributor
    CHECK (released_by IS NULL OR NOT (released_by = ANY (contributors)));
