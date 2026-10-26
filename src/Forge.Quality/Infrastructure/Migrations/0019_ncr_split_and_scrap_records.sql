-- Sprint 10 (WW-S10-24): an open NCR can be split so each part takes its own disposition (five scrapped, three reworked), and an NCR's
-- scrap disposition takes the order output back as a closed, fully scrapped record beside rework and repair. History is never rewritten.
ALTER TABLE quality.ncr ADD COLUMN parent_ncr_id bigint NULL REFERENCES quality.ncr(ncr_id);
CREATE INDEX ncr_parent ON quality.ncr (parent_ncr_id) WHERE parent_ncr_id IS NOT NULL;

ALTER TABLE quality.rework DROP CONSTRAINT rework_kind_check;
ALTER TABLE quality.rework ADD CONSTRAINT rework_kind_check CHECK (kind IN ('Rework', 'Repair', 'Scrap'));
