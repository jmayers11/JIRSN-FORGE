-- V2.5: the technical package sent with each purchase order line — the item revision and the document
-- revisions the supplier builds to — written once when the order is approved and never changed, so what
-- was transmitted stays the evidence even after an engineering change.
CREATE TABLE procurement.line_package (
    po_number      bigint NOT NULL,
    line_number    integer NOT NULL,
    part_number    text NOT NULL,
    item_revision  text NULL,
    summary        text NOT NULL,
    recorded_at    timestamptz NOT NULL,
    recorded_by    text NOT NULL,
    PRIMARY KEY (po_number, line_number),
    FOREIGN KEY (po_number, line_number) REFERENCES procurement.purchase_order_line(po_number, line_number)
);

CREATE TABLE procurement.line_package_document (
    po_number        bigint NOT NULL,
    line_number      integer NOT NULL,
    position         integer NOT NULL,
    document_number  text NOT NULL,
    title            text NOT NULL,
    revision         text NULL,
    state            text NOT NULL,
    source           text NOT NULL,
    file_sha256      text NULL,
    file_name        text NULL,
    PRIMARY KEY (po_number, line_number, position),
    FOREIGN KEY (po_number, line_number) REFERENCES procurement.line_package(po_number, line_number)
);

CREATE FUNCTION procurement.reject_package_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'technical packages are written once and never changed (%)', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER line_package_frozen BEFORE UPDATE OR DELETE ON procurement.line_package FOR EACH ROW EXECUTE FUNCTION procurement.reject_package_change();
CREATE TRIGGER line_package_document_frozen BEFORE UPDATE OR DELETE ON procurement.line_package_document FOR EACH ROW EXECUTE FUNCTION procurement.reject_package_change();
