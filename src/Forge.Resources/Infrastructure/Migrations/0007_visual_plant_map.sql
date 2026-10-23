ALTER TABLE resources.site
    ADD COLUMN plant_map_sha256 text NULL CHECK (plant_map_sha256 IS NULL OR plant_map_sha256 ~ '^[0-9a-f]{64}$'),
    ADD COLUMN plant_map_content_type text NULL CHECK (plant_map_content_type IS NULL OR plant_map_content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    ADD COLUMN plant_map_updated_at timestamptz NULL,
    ADD COLUMN plant_map_updated_by text NULL,
    ADD CONSTRAINT site_plant_map_pair CHECK ((plant_map_sha256 IS NULL) = (plant_map_content_type IS NULL));

ALTER TABLE resources.work_center
    ADD COLUMN map_x integer NULL CHECK (map_x BETWEEN 0 AND 1000),
    ADD COLUMN map_y integer NULL CHECK (map_y BETWEEN 0 AND 1000),
    ADD CONSTRAINT work_center_map_pair CHECK ((map_x IS NULL) = (map_y IS NULL));
