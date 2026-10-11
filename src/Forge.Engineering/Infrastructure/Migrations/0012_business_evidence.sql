-- Extend the existing reference attachment model; business evidence never acquires production authority.
ALTER TABLE engineering.document_link ADD COLUMN target_markings integer NOT NULL DEFAULT 0 CHECK (target_markings BETWEEN 0 AND 7);
CREATE OR REPLACE FUNCTION engineering.check_document_link() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE doc_authority text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'document links are never deleted; detach them' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD.detached_at IS NOT NULL OR
        (NEW.document_number,NEW.revision,NEW.target_type,NEW.target_id,NEW.target_label,NEW.program_id,NEW.target_markings,NEW.purpose,NEW.linked_by,NEW.linked_at)
        IS DISTINCT FROM (OLD.document_number,OLD.revision,OLD.target_type,OLD.target_id,OLD.target_label,OLD.program_id,OLD.target_markings,OLD.purpose,OLD.linked_by,OLD.linked_at)) THEN
        RAISE EXCEPTION 'a document link only changes by being detached, once' USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT authority INTO doc_authority FROM engineering.document WHERE number = NEW.document_number;
    IF doc_authority = 'Reference' AND NEW.purpose = 'Follow' THEN
        RAISE EXCEPTION 'a reference document is never production authority' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
ALTER TABLE engineering.document DROP CONSTRAINT document_doc_type_check;
ALTER TABLE engineering.document ADD CONSTRAINT document_doc_type_check CHECK
    (doc_type IN ('Drawing','Specification','Procedure','WorkInstruction','InspectionPlan','Standard','Datasheet','Certificate','PurchaseDocument','Contract','Photo','Other',
                 'PackingSlip','CertificateOfConformance','CertificateOfAnalysis','MeasurementRecord','FaiReport'));
ALTER TABLE engineering.document_link DROP CONSTRAINT document_link_target_type_check;
ALTER TABLE engineering.document_link ADD CONSTRAINT document_link_target_type_check CHECK
    (target_type IN ('Item','ItemRevision','Supplier','Program','ProductionOrder','PurchaseOrder','WorkOrder','Ncr','WorkCenter',
                    'Receipt','ReceivingInspection','Inspection','Shipment','Fai'));

-- A human's verification of a signed source file is evidence, distinct from releasing engineering instructions.
CREATE TABLE engineering.evidence_verification (
    verification_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_number text NOT NULL,
    revision text NOT NULL,
    file_sha256 text NOT NULL REFERENCES engineering.document_file(sha256),
    source_signer text NOT NULL CHECK (length(source_signer) BETWEEN 1 AND 200),
    source_signed_at timestamptz NOT NULL,
    meaning text NOT NULL CHECK (length(meaning) BETWEEN 1 AND 1000),
    verified_by text NOT NULL,
    verified_at timestamptz NOT NULL,
    FOREIGN KEY (document_number,revision) REFERENCES engineering.document_revision(document_number,revision),
    UNIQUE (document_number,revision)
);
CREATE TRIGGER evidence_verification_immutable BEFORE UPDATE OR DELETE ON engineering.evidence_verification
    FOR EACH ROW EXECUTE FUNCTION engineering.reject_file_change();
