-- V3.1: stock dimensions kept apart (V3 brief H, BS).
--
-- 1. The ledger's status column has always been the QUALITY status of stock. Its value 'Available' also read
--    as "available for planning", which it never meant: stock released by quality may sit at an outside
--    processor, in transit or in quarantine. The value is renamed 'Released'. The rename touches no quantity,
--    location, lot, actor or time; the block at the end proves every balance is unchanged and fails the
--    migration otherwise. The ledger stays append-only: the trigger is off only for this rename.
-- 2. Locations gain the attributes the other dimensions need: a place in a hierarchy, whether stock there
--    counts for planning (nettable), the work center or machine it serves (point of use), and who has custody
--    (an outside processor is a supplier holding company-owned material).

CREATE TEMP TABLE v31_before AS
SELECT part_number, location_code, lot_number, sum(quantity) AS quantity
FROM inventory.balance GROUP BY part_number, location_code, lot_number;

ALTER TABLE inventory.transaction DISABLE TRIGGER transaction_append_only;
ALTER TABLE inventory.transaction DROP CONSTRAINT IF EXISTS transaction_from_status_check;
ALTER TABLE inventory.transaction DROP CONSTRAINT IF EXISTS transaction_to_status_check;
UPDATE inventory.transaction SET from_status = 'Released' WHERE from_status = 'Available';
UPDATE inventory.transaction SET to_status = 'Released' WHERE to_status = 'Available';
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_from_status_check CHECK (from_status IN ('Released', 'Inspection', 'Hold'));
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_to_status_check CHECK (to_status IN ('Released', 'Inspection', 'Hold'));
ALTER TABLE inventory.transaction ENABLE TRIGGER transaction_append_only;

ALTER TABLE inventory.adjustment_request DROP CONSTRAINT IF EXISTS adjustment_request_status_check;
UPDATE inventory.adjustment_request SET status = 'Released' WHERE status = 'Available';
ALTER TABLE inventory.adjustment_request ADD CONSTRAINT adjustment_request_status_check CHECK (status IN ('Released', 'Inspection', 'Hold'));

DO $$
DECLARE
    differences integer;
    leftover integer;
BEGIN
    SELECT count(*) INTO differences FROM (
        SELECT part_number, location_code, lot_number, sum(quantity) AS quantity
        FROM inventory.balance GROUP BY part_number, location_code, lot_number
    ) after
    FULL JOIN v31_before before ON after.part_number = before.part_number
        AND after.location_code = before.location_code
        AND after.lot_number IS NOT DISTINCT FROM before.lot_number
    WHERE after.quantity IS DISTINCT FROM before.quantity;
    SELECT count(*) INTO leftover FROM inventory.transaction WHERE from_status = 'Available' OR to_status = 'Available';
    IF differences > 0 OR leftover > 0 THEN
        RAISE EXCEPTION 'V3.1 status rename changed % balance(s) and left % row(s) unrenamed', differences, leftover;
    END IF;
END $$;

DROP TABLE v31_before;

-- Locations.
ALTER TABLE inventory.location DROP CONSTRAINT IF EXISTS location_kind_check;
ALTER TABLE inventory.location ADD CONSTRAINT location_kind_check CHECK (kind IN (
    'Stores', 'Receiving', 'Inspection', 'Quarantine', 'Production', 'Shipping',
    'Staging', 'MachineSide', 'WipStorage', 'ExternalProcessor', 'InTransit'));

ALTER TABLE inventory.location ADD COLUMN parent_code text NULL REFERENCES inventory.location(code);
ALTER TABLE inventory.location ADD COLUMN level text NOT NULL DEFAULT 'Area'
    CHECK (level IN ('Area', 'Zone', 'Rack', 'Shelf', 'Bin', 'Position'));
-- Existing locations keep counting for planning, except quarantine, where nothing should be planned from.
ALTER TABLE inventory.location ADD COLUMN nettable boolean NOT NULL DEFAULT true;
UPDATE inventory.location SET nettable = false WHERE kind = 'Quarantine';
ALTER TABLE inventory.location ADD COLUMN work_center_code text NULL REFERENCES resources.work_center(code);
ALTER TABLE inventory.location ADD COLUMN machine_code text NULL REFERENCES resources.machine(code);
ALTER TABLE inventory.location ADD COLUMN custodian_supplier text NULL REFERENCES procurement.supplier(code);
ALTER TABLE inventory.location ADD COLUMN changed_at timestamptz NULL;
ALTER TABLE inventory.location ADD COLUMN changed_by text NULL;

ALTER TABLE inventory.location ADD CONSTRAINT location_not_own_parent CHECK (parent_code IS NULL OR parent_code <> code);
-- Material at an outside processor is company-owned in the supplier's custody; it never counts for planning.
ALTER TABLE inventory.location ADD CONSTRAINT location_external_custody
    CHECK ((kind = 'ExternalProcessor') = (custodian_supplier IS NOT NULL));
ALTER TABLE inventory.location ADD CONSTRAINT location_away_not_nettable
    CHECK (kind NOT IN ('ExternalProcessor', 'InTransit') OR NOT nettable);
-- A machine-side location serves one machine; staging serves a work center.
ALTER TABLE inventory.location ADD CONSTRAINT location_machine_side
    CHECK ((kind = 'MachineSide') = (machine_code IS NOT NULL));
ALTER TABLE inventory.location ADD CONSTRAINT location_staging_work_center
    CHECK (kind <> 'Staging' OR work_center_code IS NOT NULL);
ALTER TABLE inventory.location ADD CONSTRAINT location_point_of_use_kind
    CHECK (work_center_code IS NULL OR kind IN ('Production', 'Staging', 'MachineSide', 'WipStorage'));
CREATE INDEX location_parent ON inventory.location (parent_code) WHERE parent_code IS NOT NULL;
