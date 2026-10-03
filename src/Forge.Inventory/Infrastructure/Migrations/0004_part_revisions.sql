-- V2.3: the part revision received, on receipts, production receipts and lots (review D3). Stock received
-- before this has no revision and is reported as "revision not recorded", never assumed current.
ALTER TABLE inventory.lot ADD COLUMN part_revision text NULL;
ALTER TABLE inventory.transaction ADD COLUMN part_revision text NULL;
