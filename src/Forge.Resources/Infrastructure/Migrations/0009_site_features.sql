-- Owner decision 2026-11-01: a company can turn off the parts of Forge it does not need (customer payments and credit holds, supplier
-- invoices, shipping carriers, backflushing). A feature without a row is on.
CREATE TABLE resources.site_feature (
    code        text PRIMARY KEY CHECK (code IN ('receivables', 'supplier-invoices', 'shipping-carriers', 'backflush')),
    enabled     boolean NOT NULL,
    updated_by  text NOT NULL,
    updated_at  timestamptz NOT NULL
);
