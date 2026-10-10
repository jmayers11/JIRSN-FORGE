-- V2.11: a component installed by issuing a part to a work order keeps the ledger entry it came from, so the
-- asset's genealogy reaches its lot or serial, supplier and purchase order from the record.
ALTER TABLE maintenance.asset_component ADD COLUMN source_transaction_id bigint NULL;

CREATE FUNCTION maintenance.keep_component_source() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.source_transaction_id IS DISTINCT FROM OLD.source_transaction_id THEN
        RAISE EXCEPTION 'component % keeps the issue it was installed from', OLD.component_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER asset_component_source_kept BEFORE UPDATE ON maintenance.asset_component FOR EACH ROW EXECUTE FUNCTION maintenance.keep_component_source();
