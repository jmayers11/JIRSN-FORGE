-- V3.14: packaging and shipping materials are a distinct bought item class.
ALTER TABLE engineering.item DROP CONSTRAINT IF EXISTS item_item_class_check;
ALTER TABLE engineering.item ADD CONSTRAINT item_item_class_check
    CHECK (item_class IN ('Unclassified', 'Make', 'BuildToPrint', 'Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Mro', 'Tooling', 'Service', 'OtherControlled', 'Packaging'));

ALTER TABLE engineering.item DROP CONSTRAINT IF EXISTS item_class_bought;
ALTER TABLE engineering.item ADD CONSTRAINT item_class_bought
    CHECK (item_class NOT IN ('BuildToPrint', 'Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Service', 'Packaging') OR make_buy = 'Buy');

ALTER TABLE engineering.documentation_policy DROP CONSTRAINT IF EXISTS documentation_policy_item_class_check;
ALTER TABLE engineering.documentation_policy ADD CONSTRAINT documentation_policy_item_class_check
    CHECK (item_class IN ('Unclassified', 'Make', 'BuildToPrint', 'Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Mro', 'Tooling', 'Service', 'OtherControlled', 'Packaging'));
