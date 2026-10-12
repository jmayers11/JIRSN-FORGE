-- Governance D/I extends the existing hold policy and records explicit release evidence.
ALTER TABLE kernel.hold_kind
    ADD COLUMN require_fresh_assurance boolean NOT NULL DEFAULT false,
    ADD COLUMN require_signature boolean NOT NULL DEFAULT false,
    ADD COLUMN signature_meaning text NOT NULL DEFAULT 'I authorize release of this hold after reviewing the resolution evidence.',
    ADD COLUMN propagate_downstream boolean NOT NULL DEFAULT false,
    ADD CHECK (NOT require_signature OR require_fresh_assurance),
    ADD CHECK (length(signature_meaning) BETWEEN 1 AND 500);

CREATE TABLE kernel.hold_release_signoff (
    hold_id bigint PRIMARY KEY REFERENCES kernel.hold(hold_id),
    person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    signed_at timestamptz NOT NULL,
    record_version integer NOT NULL CHECK (record_version = 1),
    record_fingerprint text NOT NULL CHECK (length(record_fingerprint) = 64),
    policy_fingerprint text NOT NULL CHECK (length(policy_fingerprint) = 64),
    evidence text NOT NULL
);

CREATE FUNCTION kernel.hold_signoff_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'hold release signatures are append-only';
END $$;
CREATE TRIGGER hold_signoff_append_only BEFORE UPDATE OR DELETE ON kernel.hold_release_signoff
    FOR EACH ROW EXECUTE FUNCTION kernel.hold_signoff_append_only();
