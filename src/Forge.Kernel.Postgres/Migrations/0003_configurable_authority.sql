-- V3.15 configurable authority: role definitions and permission grants are data.
CREATE TABLE kernel.role_definition (
    role_name text PRIMARY KEY,
    display_name text NOT NULL DEFAULT '',
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE kernel.permission_catalog (
    permission text PRIMARY KEY,
    description text NOT NULL DEFAULT ''
);
CREATE TABLE kernel.role_permission (
    role_name text NOT NULL REFERENCES kernel.role_definition(role_name),
    permission text NOT NULL REFERENCES kernel.permission_catalog(permission),
    granted_at timestamptz NOT NULL DEFAULT now(),
    granted_by text NOT NULL DEFAULT 'system:migration',
    PRIMARY KEY (role_name, permission)
);
CREATE TABLE kernel.role_permission_scope (
    role_name text NOT NULL,
    permission text NOT NULL,
    scope_type text NOT NULL CHECK (scope_type IN ('program','facility','work_center','contract','customer','shift')),
    scope_id text NOT NULL,
    PRIMARY KEY (role_name, permission, scope_type, scope_id),
    FOREIGN KEY (role_name, permission) REFERENCES kernel.role_permission(role_name, permission) ON DELETE CASCADE
);ALTER TABLE kernel.role_assignment ADD COLUMN expires_at timestamptz NULL;
CREATE INDEX role_assignment_active ON kernel.role_assignment (person_id, role) WHERE expires_at IS NULL;
INSERT INTO kernel.permission_catalog (permission) VALUES ('competency.certify') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('competency.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('competency.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.alternates.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.alternates.propose') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.changes.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.changes.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.changes.review') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.cots.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.cots.propose') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.documents.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.documents.attach') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.documents.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.documents.release') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.obsolescence.record') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.planning.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('engineering.release') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('execution.orders.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('execution.orders.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('execution.orders.release') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('execution.reasons.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('execution.rootcause.confirm') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('execution.shopfloor.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('execution.shopfloor.record') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('holds.place') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('holds.production.release') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('holds.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('inventory.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('inventory.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('inventory.mro.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('inventory.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('inventory.shipping.ship') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('inventory.spares.issue') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('inventory.transact') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('kernel.access.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('kernel.audit.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('kernel.people.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.alternates.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.assets.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.downtime.report') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.outage.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.plan') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.pm.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('maintenance.work') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.analyst.use') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.constraints.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.demand.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.goals.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.mrp.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.readiness.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.reports.save') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.schedule.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.schedule.propose') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('planning.schedule.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('procurement.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('procurement.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('procurement.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('procurement.requisition.raise') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('programs.directory') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('programs.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('programs.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('projects.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('projects.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('projects.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('quality.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('quality.disposition') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('quality.inspect') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('quality.plan') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('quality.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('resources.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('resources.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('resources.tools.calibrate') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('resources.tools.handle') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('resources.tools.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('safety.rules.approve') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('safety.rules.draft') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('safety.rules.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('workforce.attendance.record') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('workforce.badges.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('workforce.manage') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('workforce.read') ON CONFLICT DO NOTHING;
INSERT INTO kernel.permission_catalog (permission) VALUES ('workforce.supervise') ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition (role_name) VALUES
    ('Auditor'),
    ('Buyer'),
    ('Certifier'),
    ('ChangeBoard'),
    ('DocumentController'),
    ('EngineeringApprover'),
    ('FacilitiesManager'),
    ('Inspector'),
    ('InventoryController'),
    ('MaintenancePlanner'),
    ('MaintenanceTechnician'),
    ('ManufacturingEngineer'),
    ('MaterialHandler'),
    ('Planner'),
    ('ProgramManager'),
    ('PurchasingManager'),
    ('QualityEngineer'),
    ('QualityManager'),
    ('SafetyApprover'),
    ('SafetyEngineer'),
    ('SecurityAdministrator'),
    ('Shipper'),
    ('ShopFloorTerminal'),
    ('Supervisor'),
    ('TrainingCoordinator')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission (role_name, permission) VALUES
('Auditor','holds.read'),
('Auditor','kernel.audit.read'),
('Auditor','kernel.people.read'),
('Auditor','projects.read'),
('Buyer','engineering.alternates.propose'),
('Buyer','engineering.changes.review'),
('Buyer','engineering.cots.propose'),
('Buyer','engineering.documents.attach'),
('Buyer','engineering.obsolescence.record'),
('Buyer','engineering.read'),
('Buyer','execution.orders.read'),
('Buyer','holds.place'),
('Buyer','holds.read'),
('Buyer','inventory.read'),
('Buyer','planning.analyst.use'),
('Buyer','planning.mrp.read'),
('Buyer','planning.reports.save'),
('Buyer','procurement.manage'),
('Buyer','procurement.read'),
('Buyer','procurement.requisition.raise'),
('Buyer','projects.read'),
('Certifier','competency.certify'),
('Certifier','competency.read'),
('ChangeBoard','engineering.alternates.approve'),
('ChangeBoard','engineering.changes.approve'),
('ChangeBoard','engineering.changes.review'),
('ChangeBoard','engineering.read'),
('ChangeBoard','execution.orders.read'),
('ChangeBoard','holds.place'),
('ChangeBoard','holds.read'),
('ChangeBoard','inventory.read'),
('ChangeBoard','planning.analyst.use'),
('ChangeBoard','planning.reports.save'),
('ChangeBoard','procurement.read'),
('ChangeBoard','programs.read'),
('ChangeBoard','quality.read'),
('ChangeBoard','resources.read'),
('DocumentController','engineering.changes.manage'),
('DocumentController','engineering.changes.review'),
('DocumentController','engineering.documents.approve'),
('DocumentController','engineering.documents.attach'),
('DocumentController','engineering.documents.manage'),
('DocumentController','engineering.documents.release'),
('DocumentController','engineering.read'),
('DocumentController','engineering.release'),
('DocumentController','execution.orders.read'),
('DocumentController','maintenance.read'),
('DocumentController','planning.analyst.use'),
('DocumentController','planning.reports.save'),
('DocumentController','procurement.read'),
('DocumentController','programs.read'),
('DocumentController','quality.read'),
('DocumentController','resources.read'),
('EngineeringApprover','engineering.alternates.approve'),
('EngineeringApprover','engineering.changes.approve'),
('EngineeringApprover','engineering.changes.review'),
('EngineeringApprover','engineering.cots.manage'),
('EngineeringApprover','engineering.documents.approve'),
('EngineeringApprover','engineering.documents.release'),
('EngineeringApprover','engineering.read'),
('EngineeringApprover','engineering.release'),
('EngineeringApprover','execution.orders.read'),
('EngineeringApprover','holds.place'),
('EngineeringApprover','holds.read'),
('EngineeringApprover','programs.read'),
('EngineeringApprover','resources.read'),
('FacilitiesManager','engineering.documents.attach'),
('FacilitiesManager','engineering.read'),
('FacilitiesManager','inventory.read'),
('FacilitiesManager','maintenance.read'),
('FacilitiesManager','planning.analyst.use'),
('FacilitiesManager','planning.demand.manage'),
('FacilitiesManager','planning.mrp.read'),
('FacilitiesManager','planning.reports.save'),
('FacilitiesManager','planning.schedule.read'),
('FacilitiesManager','procurement.read'),
('FacilitiesManager','procurement.requisition.raise'),
('FacilitiesManager','projects.approve'),
('FacilitiesManager','projects.manage'),
('FacilitiesManager','projects.read'),
('FacilitiesManager','resources.read'),
('Inspector','engineering.documents.attach'),
('Inspector','engineering.read'),
('Inspector','execution.orders.read'),
('Inspector','execution.shopfloor.read'),
('Inspector','holds.place'),
('Inspector','holds.read'),
('Inspector','inventory.read'),
('Inspector','planning.analyst.use'),
('Inspector','planning.reports.save'),
('Inspector','quality.inspect'),
('Inspector','quality.read'),
('Inspector','resources.read'),
('InventoryController','engineering.read'),
('InventoryController','execution.orders.read'),
('InventoryController','holds.place'),
('InventoryController','holds.read'),
('InventoryController','inventory.approve'),
('InventoryController','inventory.manage'),
('InventoryController','inventory.mro.manage'),
('InventoryController','inventory.read'),
('InventoryController','planning.analyst.use'),
('InventoryController','planning.mrp.read'),
('InventoryController','planning.reports.save'),
('InventoryController','procurement.read'),
('InventoryController','projects.read'),
('InventoryController','resources.read'),
('MaintenancePlanner','engineering.alternates.propose'),
('MaintenancePlanner','engineering.changes.review'),
('MaintenancePlanner','engineering.cots.propose'),
('MaintenancePlanner','engineering.documents.attach'),
('MaintenancePlanner','engineering.obsolescence.record'),
('MaintenancePlanner','engineering.read'),
('MaintenancePlanner','execution.rootcause.confirm'),
('MaintenancePlanner','holds.read'),
('MaintenancePlanner','inventory.mro.manage'),
('MaintenancePlanner','inventory.read'),
('MaintenancePlanner','maintenance.alternates.approve'),
('MaintenancePlanner','maintenance.approve'),
('MaintenancePlanner','maintenance.assets.manage'),
('MaintenancePlanner','maintenance.downtime.report'),
('MaintenancePlanner','maintenance.outage.approve'),
('MaintenancePlanner','maintenance.plan'),
('MaintenancePlanner','maintenance.pm.manage'),
('MaintenancePlanner','maintenance.read'),
('MaintenancePlanner','planning.analyst.use'),
('MaintenancePlanner','planning.demand.manage'),
('MaintenancePlanner','planning.reports.save'),
('MaintenancePlanner','planning.schedule.read'),
('MaintenancePlanner','procurement.requisition.raise'),
('MaintenancePlanner','projects.manage'),
('MaintenancePlanner','projects.read'),
('MaintenancePlanner','resources.read'),
('MaintenancePlanner','resources.tools.handle'),
('MaintenancePlanner','resources.tools.manage'),
('MaintenanceTechnician','engineering.documents.attach'),
('MaintenanceTechnician','engineering.read'),
('MaintenanceTechnician','inventory.read'),
('MaintenanceTechnician','inventory.spares.issue'),
('MaintenanceTechnician','maintenance.downtime.report'),
('MaintenanceTechnician','maintenance.plan'),
('MaintenanceTechnician','maintenance.read'),
('MaintenanceTechnician','maintenance.work'),
('MaintenanceTechnician','planning.analyst.use'),
('MaintenanceTechnician','planning.reports.save'),
('MaintenanceTechnician','resources.read'),
('MaintenanceTechnician','resources.tools.calibrate'),
('MaintenanceTechnician','resources.tools.handle'),
('ManufacturingEngineer','competency.read'),
('ManufacturingEngineer','engineering.alternates.propose'),
('ManufacturingEngineer','engineering.changes.manage'),
('ManufacturingEngineer','engineering.changes.review'),
('ManufacturingEngineer','engineering.cots.propose'),
('ManufacturingEngineer','engineering.documents.attach'),
('ManufacturingEngineer','engineering.documents.manage'),
('ManufacturingEngineer','engineering.manage'),
('ManufacturingEngineer','engineering.obsolescence.record'),
('ManufacturingEngineer','engineering.planning.manage'),
('ManufacturingEngineer','engineering.read'),
('ManufacturingEngineer','execution.orders.manage'),
('ManufacturingEngineer','execution.orders.read'),
('ManufacturingEngineer','execution.reasons.manage'),
('ManufacturingEngineer','execution.rootcause.confirm'),
('ManufacturingEngineer','execution.shopfloor.read'),
('ManufacturingEngineer','holds.place'),
('ManufacturingEngineer','holds.read'),
('ManufacturingEngineer','inventory.read'),
('ManufacturingEngineer','maintenance.assets.manage'),
('ManufacturingEngineer','maintenance.read'),
('ManufacturingEngineer','planning.analyst.use'),
('ManufacturingEngineer','planning.mrp.read'),
('ManufacturingEngineer','planning.readiness.read'),
('ManufacturingEngineer','planning.reports.save'),
('ManufacturingEngineer','planning.schedule.read'),
('ManufacturingEngineer','procurement.read'),
('ManufacturingEngineer','procurement.requisition.raise'),
('ManufacturingEngineer','projects.manage'),
('ManufacturingEngineer','projects.read'),
('ManufacturingEngineer','quality.read'),
('ManufacturingEngineer','resources.manage'),
('ManufacturingEngineer','resources.read'),
('ManufacturingEngineer','resources.tools.manage'),
('ManufacturingEngineer','safety.rules.read'),
('ManufacturingEngineer','workforce.manage'),
('ManufacturingEngineer','workforce.read'),
('MaterialHandler','engineering.read'),
('MaterialHandler','execution.orders.read'),
('MaterialHandler','holds.read'),
('MaterialHandler','inventory.read'),
('MaterialHandler','inventory.spares.issue'),
('MaterialHandler','inventory.transact'),
('MaterialHandler','maintenance.read'),
('MaterialHandler','planning.analyst.use'),
('MaterialHandler','planning.mrp.read'),
('MaterialHandler','planning.reports.save'),
('MaterialHandler','procurement.read'),
('MaterialHandler','projects.read'),
('MaterialHandler','quality.read'),
('MaterialHandler','resources.read'),
('MaterialHandler','resources.tools.handle'),
('Planner','engineering.changes.review'),
('Planner','engineering.documents.attach'),
('Planner','engineering.planning.manage'),
('Planner','engineering.read'),
('Planner','execution.orders.manage'),
('Planner','execution.orders.read'),
('Planner','execution.orders.release'),
('Planner','execution.shopfloor.read'),
('Planner','holds.place'),
('Planner','holds.production.release'),
('Planner','holds.read'),
('Planner','inventory.mro.manage'),
('Planner','inventory.read'),
('Planner','maintenance.outage.approve'),
('Planner','maintenance.read'),
('Planner','planning.analyst.use'),
('Planner','planning.constraints.manage'),
('Planner','planning.demand.manage'),
('Planner','planning.goals.manage'),
('Planner','planning.mrp.read'),
('Planner','planning.readiness.read'),
('Planner','planning.reports.save'),
('Planner','planning.schedule.approve'),
('Planner','planning.schedule.propose'),
('Planner','planning.schedule.read'),
('Planner','procurement.read'),
('Planner','procurement.requisition.raise'),
('Planner','programs.read'),
('Planner','projects.read'),
('Planner','quality.read'),
('Planner','resources.read'),
('Planner','safety.rules.read'),
('Planner','workforce.read'),
('ProgramManager','engineering.changes.review'),
('ProgramManager','engineering.documents.attach'),
('ProgramManager','engineering.read'),
('ProgramManager','execution.orders.read'),
('ProgramManager','holds.place'),
('ProgramManager','holds.read'),
('ProgramManager','inventory.read'),
('ProgramManager','planning.analyst.use'),
('ProgramManager','planning.goals.manage'),
('ProgramManager','planning.mrp.read'),
('ProgramManager','planning.reports.save'),
('ProgramManager','planning.schedule.read'),
('ProgramManager','procurement.requisition.raise'),
('ProgramManager','programs.manage'),
('ProgramManager','programs.read'),
('ProgramManager','projects.read'),
('ProgramManager','quality.read'),
('PurchasingManager','engineering.alternates.propose'),
('PurchasingManager','engineering.changes.review'),
('PurchasingManager','engineering.documents.attach'),
('PurchasingManager','engineering.read'),
('PurchasingManager','holds.place'),
('PurchasingManager','holds.read'),
('PurchasingManager','inventory.read'),
('PurchasingManager','planning.analyst.use'),
('PurchasingManager','planning.mrp.read'),
('PurchasingManager','planning.reports.save'),
('PurchasingManager','procurement.approve'),
('PurchasingManager','procurement.read'),
('PurchasingManager','procurement.requisition.raise'),
('PurchasingManager','projects.read'),
('QualityEngineer','engineering.alternates.propose'),
('QualityEngineer','engineering.changes.review'),
('QualityEngineer','engineering.documents.attach'),
('QualityEngineer','engineering.documents.manage'),
('QualityEngineer','engineering.read'),
('QualityEngineer','execution.orders.read'),
('QualityEngineer','execution.rootcause.confirm'),
('QualityEngineer','execution.shopfloor.read'),
('QualityEngineer','holds.place'),
('QualityEngineer','holds.read'),
('QualityEngineer','inventory.read'),
('QualityEngineer','planning.analyst.use'),
('QualityEngineer','planning.demand.manage'),
('QualityEngineer','planning.reports.save'),
('QualityEngineer','procurement.requisition.raise'),
('QualityEngineer','quality.disposition'),
('QualityEngineer','quality.inspect'),
('QualityEngineer','quality.plan'),
('QualityEngineer','quality.read'),
('QualityEngineer','resources.read'),
('QualityEngineer','resources.tools.calibrate'),
('QualityEngineer','resources.tools.handle'),
('QualityEngineer','resources.tools.manage'),
('QualityManager','engineering.changes.review'),
('QualityManager','engineering.read'),
('QualityManager','execution.orders.read'),
('QualityManager','holds.place'),
('QualityManager','holds.read'),
('QualityManager','inventory.read'),
('QualityManager','planning.analyst.use'),
('QualityManager','planning.reports.save'),
('QualityManager','quality.approve'),
('QualityManager','quality.read'),
('QualityManager','resources.read'),
('SafetyApprover','competency.read'),
('SafetyApprover','holds.place'),
('SafetyApprover','holds.read'),
('SafetyApprover','resources.read'),
('SafetyApprover','safety.rules.approve'),
('SafetyApprover','safety.rules.read'),
('SafetyEngineer','competency.read'),
('SafetyEngineer','holds.place'),
('SafetyEngineer','holds.read'),
('SafetyEngineer','resources.read'),
('SafetyEngineer','safety.rules.draft'),
('SafetyEngineer','safety.rules.read'),
('SecurityAdministrator','holds.place'),
('SecurityAdministrator','holds.read'),
('SecurityAdministrator','kernel.access.manage'),
('SecurityAdministrator','kernel.audit.read'),
('SecurityAdministrator','kernel.people.read'),
('SecurityAdministrator','programs.directory'),
('SecurityAdministrator','workforce.badges.manage'),
('Shipper','engineering.read'),
('Shipper','execution.orders.read'),
('Shipper','holds.place'),
('Shipper','holds.read'),
('Shipper','inventory.read'),
('Shipper','inventory.shipping.ship'),
('Shipper','planning.analyst.use'),
('Shipper','planning.mrp.read'),
('Shipper','planning.reports.save'),
('Shipper','quality.read'),
('ShopFloorTerminal','execution.orders.read'),
('ShopFloorTerminal','execution.shopfloor.read'),
('ShopFloorTerminal','execution.shopfloor.record'),
('ShopFloorTerminal','planning.readiness.read'),
('ShopFloorTerminal','planning.schedule.read'),
('ShopFloorTerminal','resources.read'),
('ShopFloorTerminal','workforce.attendance.record'),
('Supervisor','competency.read'),
('Supervisor','engineering.changes.review'),
('Supervisor','engineering.documents.attach'),
('Supervisor','engineering.read'),
('Supervisor','execution.orders.read'),
('Supervisor','execution.shopfloor.read'),
('Supervisor','execution.shopfloor.record'),
('Supervisor','holds.place'),
('Supervisor','holds.production.release'),
('Supervisor','holds.read'),
('Supervisor','inventory.read'),
('Supervisor','maintenance.downtime.report'),
('Supervisor','maintenance.read'),
('Supervisor','planning.analyst.use'),
('Supervisor','planning.constraints.manage'),
('Supervisor','planning.readiness.read'),
('Supervisor','planning.reports.save'),
('Supervisor','planning.schedule.read'),
('Supervisor','quality.read'),
('Supervisor','resources.read'),
('Supervisor','resources.tools.handle'),
('Supervisor','safety.rules.read'),
('Supervisor','workforce.attendance.record'),
('Supervisor','workforce.read'),
('Supervisor','workforce.supervise'),
('TrainingCoordinator','competency.manage'),
('TrainingCoordinator','competency.read'),
('TrainingCoordinator','engineering.read'),
('TrainingCoordinator','resources.read'),
('TrainingCoordinator','workforce.read')
ON CONFLICT DO NOTHING;





CREATE TABLE kernel.separation_policy (
    policy_id text PRIMARY KEY,
    description text NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by text NOT NULL DEFAULT 'system:migration'
);
INSERT INTO kernel.separation_policy (policy_id, description) VALUES
    ('access.self_role_change', 'A person cannot assign or revoke their own roles.'),
    ('access.self_program_change', 'A person cannot grant or revoke their own program access.')
ON CONFLICT DO NOTHING;
CREATE TABLE kernel.approval_threshold_policy (
    permission text PRIMARY KEY REFERENCES kernel.permission_catalog(permission),
    maximum_amount numeric(18,2) NOT NULL CHECK (maximum_amount >= 0),
    currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    updated_at timestamptz NOT NULL,
    updated_by text NOT NULL
);
