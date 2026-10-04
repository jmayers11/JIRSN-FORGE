-- V2.7: demand from every source enters netting with its source: maintenance, MRO replenishment, facility
-- and capital projects, quality, tooling and engineering, beside customer, forecast, service, internal and
-- contract demand. These kinds always name their source (kind:key, e.g. work-order:57).
ALTER TABLE planning.demand DROP CONSTRAINT demand_kind_check;
ALTER TABLE planning.demand ADD CONSTRAINT demand_kind_check CHECK (kind IN ('CustomerOrder', 'Forecast', 'Service', 'Internal', 'ContractDelivery',
    'Maintenance', 'MroReplenishment', 'FacilityProject', 'CapitalProject', 'Quality', 'Tooling', 'Engineering'));
ALTER TABLE planning.demand ADD CONSTRAINT demand_source_named CHECK (kind IN ('CustomerOrder', 'Forecast', 'Service', 'Internal', 'ContractDelivery') OR source_key IS NOT NULL);
