-- V2.3: inspection plans per item revision (review D4). '*' is the plan for every revision of the part,
-- which is what every existing plan becomes; a plan for one revision takes precedence for that revision.
ALTER TABLE quality.characteristic DROP CONSTRAINT characteristic_part_number_sequence_fkey;
ALTER TABLE quality.characteristic DROP CONSTRAINT characteristic_pkey;
ALTER TABLE quality.inspection_plan DROP CONSTRAINT inspection_plan_pkey;
ALTER TABLE quality.inspection_plan ADD COLUMN revision text NOT NULL DEFAULT '*';
ALTER TABLE quality.characteristic ADD COLUMN revision text NOT NULL DEFAULT '*';
ALTER TABLE quality.inspection_plan ADD PRIMARY KEY (part_number, sequence, revision);
ALTER TABLE quality.characteristic ADD PRIMARY KEY (part_number, sequence, revision, number);
ALTER TABLE quality.characteristic ADD FOREIGN KEY (part_number, sequence, revision)
    REFERENCES quality.inspection_plan(part_number, sequence, revision) ON DELETE CASCADE;
