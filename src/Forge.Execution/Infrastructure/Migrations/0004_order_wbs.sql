-- The program WBS element an order is charged to (Programs owns the WBS; the order names it).
ALTER TABLE execution.production_order ADD COLUMN wbs_code text NULL;
