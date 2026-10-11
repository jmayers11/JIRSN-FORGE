ALTER TABLE engineering.document_link DROP CONSTRAINT document_link_target_type_check;
ALTER TABLE engineering.document_link ADD CONSTRAINT document_link_target_type_check CHECK
    (target_type IN ('Item','ItemRevision','Supplier','Program','ProductionOrder','PurchaseOrder','WorkOrder','Ncr','WorkCenter',
                    'Receipt','ReceivingInspection','Inspection','Shipment','Fai','Rfq','SupplierQuote'));
