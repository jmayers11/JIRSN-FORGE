// Role names as people read them. The server's role catalog is authoritative; unknown roles show their code.
export const ROLE_TITLES = {
  SecurityAdministrator: 'Security Administrator', ManufacturingEngineer: 'Manufacturing Engineer', Planner: 'Planner',
  Supervisor: 'Supervisor', SafetyEngineer: 'Safety Engineer', SafetyApprover: 'Safety Approver', Certifier: 'Certifier',
  TrainingCoordinator: 'Training Coordinator', EngineeringApprover: 'Engineering Approver', MaintenanceTechnician: 'Maintenance Technician',
  Auditor: 'Auditor', MaterialHandler: 'Material Handler', InventoryController: 'Inventory Controller', Buyer: 'Buyer',
  PurchasingManager: 'Purchasing Manager', ShopFloorTerminal: 'Shop Floor Terminal', Inspector: 'Inspector',
  QualityEngineer: 'Quality Engineer', QualityManager: 'Quality Manager', CustomerCommunicationsApprover: 'Customer Communications Approver', Shipper: 'Shipper',
  QmsAuthor: 'QMS Author', QmsReviewer: 'QMS Reviewer', QmsApprover: 'QMS Approver',
  QmsPolicyConsumer: 'QMS Policy Consumer', LabelAuthor: 'Label Author', LabelApprover: 'Label Approver',
  LabelPrinter: 'Label Printer', IdentificationAdministrator: 'Identification Administrator', ScanOperator: 'Scan Operator',
  ProgramManager: 'Program Manager', MaintenancePlanner: 'Maintenance Planner', FacilitiesManager: 'Facilities Manager',
  DocumentController: 'Document Controller', ChangeBoard: 'Change Board',
  AuditPlanner: 'Audit Planner', InternalAuditor: 'Internal Auditor', AuditApprover: 'Audit Approver',
  TechnicalTrainer: 'Technical Trainer', HumanResources: 'Human Resources',
};
export const roleTitle = r => ROLE_TITLES[r] ?? r;
export const ROLE_NAMES = Object.keys(ROLE_TITLES);
