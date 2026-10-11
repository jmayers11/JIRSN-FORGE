-- V3.14: shipments may explicitly require packing materials before packing can be certified.
ALTER TABLE inventory.shipment ADD COLUMN packaging_required boolean NOT NULL DEFAULT false;
