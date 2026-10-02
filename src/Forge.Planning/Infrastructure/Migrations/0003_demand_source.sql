-- Demand created from another record (a contract delivery) carries that record's key, so it is
-- planned once and can be brought back in step when the contract changes.
ALTER TABLE planning.demand ADD COLUMN source_key text NULL;
CREATE UNIQUE INDEX demand_source ON planning.demand (source_key) WHERE source_key IS NOT NULL;
