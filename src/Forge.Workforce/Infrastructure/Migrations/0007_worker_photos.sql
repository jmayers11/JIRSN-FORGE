ALTER TABLE workforce.worker
    ADD COLUMN photo_sha256 text NULL CHECK (photo_sha256 IS NULL OR photo_sha256 ~ '^[0-9a-f]{64}$'),
    ADD COLUMN photo_content_type text NULL CHECK (photo_content_type IS NULL OR photo_content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    ADD COLUMN photo_updated_at timestamptz NULL,
    ADD COLUMN photo_updated_by text NULL,
    ADD CONSTRAINT worker_photo_pair CHECK ((photo_sha256 IS NULL) = (photo_content_type IS NULL));
