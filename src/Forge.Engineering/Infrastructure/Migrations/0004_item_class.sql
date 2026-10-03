-- V2.1: item classification (V2 §10) and manufacturer parts for COTS items (§12).
-- Existing Make items become Make; every other item starts Unclassified and is worked off from a list,
-- never guessed (V2 review, migration risks).
ALTER TABLE engineering.item
    ADD COLUMN item_class text NOT NULL DEFAULT 'Unclassified'
        CHECK (item_class IN ('Unclassified', 'Make', 'BuildToPrint', 'Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Mro', 'Tooling', 'Service', 'OtherControlled')),
    ADD COLUMN classified_at timestamptz NULL,
    ADD COLUMN classified_by text NULL;
UPDATE engineering.item SET item_class = 'Make' WHERE make_buy = 'Make';
ALTER TABLE engineering.item
    ADD CONSTRAINT item_class_make CHECK (item_class <> 'Make' OR make_buy = 'Make'),
    ADD CONSTRAINT item_class_bought CHECK (item_class NOT IN ('BuildToPrint', 'Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Service') OR make_buy = 'Buy');

-- A manufacturer's part that may be bought as the item. Proposed by one person, approved by another.
CREATE TABLE engineering.manufacturer_part (
    part_number            text NOT NULL REFERENCES engineering.item(part_number),
    manufacturer           text NOT NULL,
    mpn                    text NOT NULL,
    manufacturer_revision  text NULL,
    lifecycle              text NOT NULL CHECK (lifecycle IN ('Active', 'NotRecommended', 'LastTimeBuy', 'EndOfLife', 'Unknown')),
    status                 text NOT NULL CHECK (status IN ('Proposed', 'Approved', 'Withdrawn')),
    datasheet              text NULL,
    note                   text NULL,
    proposed_by            text NOT NULL,
    proposed_at            timestamptz NOT NULL,
    approved_by            text NULL,
    approved_at            timestamptz NULL,
    withdrawn_by           text NULL,
    withdrawn_at           timestamptz NULL,
    lifecycle_changed_at   timestamptz NULL,
    PRIMARY KEY (part_number, manufacturer, mpn),
    CHECK (approved_by IS NULL OR approved_by <> proposed_by),
    CHECK (status <> 'Approved' OR approved_by IS NOT NULL)
);
