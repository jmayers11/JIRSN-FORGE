-- V2.1: supplier master (V2 review, conflict 5). Existing purchase orders keep their supplier text and
-- are linked to a supplier record made from that text, so no order loses or changes its supplier.
CREATE TABLE procurement.supplier (
    code        text PRIMARY KEY CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,39}$'),
    name        text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    status      text NOT NULL CHECK (status IN ('Active', 'OnHold', 'Inactive')),
    notes       text NULL,
    created_at  timestamptz NOT NULL,
    created_by  text NOT NULL,
    changed_at  timestamptz NULL,
    changed_by  text NULL
);
CREATE UNIQUE INDEX supplier_name ON procurement.supplier (lower(name));

INSERT INTO procurement.supplier (code, name, status, notes, created_at, created_by)
SELECT DISTINCT ON (code) code, name, 'Active', 'Created from existing purchase orders when the supplier master was introduced.', now(), 'system:migration'
FROM (
    SELECT left(COALESCE(NULLIF(trim(both '-' FROM upper(regexp_replace(trim(supplier), '[^A-Za-z0-9]+', '-', 'g'))), ''), 'SUPPLIER'), 40) AS code, trim(supplier) AS name, min(po_number) AS first_po
    FROM procurement.purchase_order GROUP BY 1, 2
) s
ORDER BY code, first_po
ON CONFLICT DO NOTHING;

ALTER TABLE procurement.purchase_order ADD COLUMN supplier_code text NULL REFERENCES procurement.supplier(code);
UPDATE procurement.purchase_order p SET supplier_code = left(COALESCE(NULLIF(trim(both '-' FROM upper(regexp_replace(trim(p.supplier), '[^A-Za-z0-9]+', '-', 'g'))), ''), 'SUPPLIER'), 40);
ALTER TABLE procurement.purchase_order ALTER COLUMN supplier_code SET NOT NULL;

-- Approved sources: when an item has any, it is bought only from them.
CREATE TABLE procurement.approved_source (
    part_number    text NOT NULL REFERENCES engineering.item(part_number),
    supplier_code  text NOT NULL REFERENCES procurement.supplier(code),
    note           text NULL,
    approved_by    text NOT NULL,
    approved_at    timestamptz NOT NULL,
    PRIMARY KEY (part_number, supplier_code)
);
