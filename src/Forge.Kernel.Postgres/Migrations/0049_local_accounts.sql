-- Owner decision 2026-11-03: an installed Forge has its own accounts (alongside any OIDC provider). A person's local credential is a
-- salted password hash; failed attempts lock the account for a while; an administrator's reset makes the person choose a new password.
CREATE TABLE kernel.local_credential (
    idp_subject      text PRIMARY KEY REFERENCES kernel.person(idp_subject),
    password_hash    text NOT NULL,
    must_change      boolean NOT NULL,
    failed_attempts  integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until     timestamptz NULL,
    changed_at       timestamptz NOT NULL,
    changed_by       text NOT NULL,
    last_sign_in_at  timestamptz NULL
);
