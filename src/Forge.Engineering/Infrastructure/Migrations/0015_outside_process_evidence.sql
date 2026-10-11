-- V3.14: processor certificates, CoCs, measurements and other business evidence attach to the dispatch/return identity.
ALTER TABLE engineering.document_link DROP CONSTRAINT document_link_target_type_check;
ALTER TABLE engineering.document_link ADD CONSTRAINT document_link_target_type_check
    CHECK (target_type IN ('Item','ItemRevision','Supplier','Program','ProductionOrder','PurchaseOrder','WorkOrder','Ncr','WorkCenter',
                           'Receipt','ReceivingInspection','Inspection','Shipment','OutsideProcess','Fai','Rfq','SupplierQuote'));
