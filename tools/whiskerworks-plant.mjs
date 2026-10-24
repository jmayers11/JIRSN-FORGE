// Fictional WhiskerWorks demo plant foundation. This is seed data, not operating instructions.
export const site = { code: 'WW-P01', name: 'WhiskerWorks Precision Manufacturing — Purrcision Plant 01', timeZone: 'America/New_York' };

export const zones = [
  ['RCV', 'Dock / Receiving'], ['RAW', 'Raw Materials Warehouse'], ['CNC', 'CNC Machining'], ['FAB', 'Fabrication'],
  ['ELE', 'Electronics Assembly'], ['SFT', 'Soft Goods & Spool Cutting'], ['KIT', 'Kitting / Staging'], ['ASM', 'Final Assembly'],
  ['QAL', 'Quality / Metrology & MRB'], ['TST', 'Functional Test'], ['PKG', 'Packaging'], ['SHP', 'Finished Goods / Shipping'],
  ['MNT', 'Maintenance / Tooling'], ['FAC', 'Facilities / Utilities'], ['OFF', 'Offices / Shift Command'],
].map(([code, name]) => ({ code, name }));

export const workCenters = [
  ['WC-RCV', 'RCV', 'Receiving', 110, 180, 'raw-material'],
  ['WC-RAW', 'RAW', 'Raw Materials', 280, 180, 'raw-material'],
  ['WC-CNC-MILL', 'CNC', 'CNC Mills', 480, 180, 'fabrication'],
  ['WC-CNC-LATHE', 'CNC', 'CNC Lathe', 650, 180, 'fabrication'],
  ['WC-FAB', 'FAB', 'Fabrication', 830, 180, 'fabrication'],
  ['WC-ELE-PCB', 'ELE', 'PCB Assembly', 110, 480, 'assembly'],
  ['WC-ELE-HARN', 'ELE', 'Harness Assembly', 280, 480, 'assembly'],
  ['WC-SFT', 'SFT', 'Soft Goods & Spool Cutting', 460, 480, 'plain'],
  ['WC-KIT', 'KIT', 'Kitting / Staging', 640, 480, 'raw-material'],
  ['WC-ASM-STD', 'ASM', 'Standard Assembly', 820, 480, 'assembly'],
  ['WC-ASM-HVY', 'ASM', 'Heavy Assembly', 920, 480, 'assembly'],
  ['WC-QAL', 'QAL', 'Quality / Metrology & MRB', 110, 780, 'inspection'],
  ['WC-TST', 'TST', 'Functional Test', 290, 780, 'inspection'],
  ['WC-PKG', 'PKG', 'Packaging', 470, 780, 'shipping'],
  ['WC-SHP', 'SHP', 'Finished Goods / Shipping', 640, 780, 'shipping'],
  ['WC-MNT', 'MNT', 'Maintenance / Tooling', 800, 780, 'plain'],
].map(([code, zoneCode, name, mapX, mapY, picture], index) => ({
  code, zoneCode, name, mapX, mapY, picture, layoutX: index % 4, layoutY: Math.floor(index / 4),
}));

// Twenty-four capacity-bearing production/test machines. Fixed docks, racks, lanes, benches,
// cages and utilities are separate non-machine assets and never add machine capacity.
export const machines = [
  ['CNC-01', 'Three-axis mill', 'WC-CNC-MILL', 'CNC_MILL', ['QUAL-CNC-MILL']],
  ['CNC-02', 'Five-axis mill', 'WC-CNC-MILL', 'CNC_MILL', ['QUAL-CNC-MILL']],
  ['CNC-03', 'Precision lathe', 'WC-CNC-LATHE', 'CNC_LATHE', ['QUAL-CNC-LATHE']],
  ['FAB-SAW-01', 'Band saw', 'WC-FAB', 'FAB_CUT', ['QUAL-FAB-CUT']],
  ['FAB-BRAKE-01', 'Press brake', 'WC-FAB', 'FAB_FORM', ['QUAL-FAB-CUT']],
  ['FAB-WELD-01', 'Heavy frame joiner', 'WC-FAB', 'FAB_JOIN', ['QUAL-FAB-JOIN']],
  ['FAB-DEBURR-01', 'Deburr station', 'WC-FAB', 'FAB_DEBURR', ['QUAL-FAB-CUT']],
  ['ELE-SMT-01', 'SMT assembly line', 'WC-ELE-PCB', 'PCB_ASSEMBLE', ['QUAL-IPC-A610']],
  ['ELE-SOLD-01', 'Precision solder bench 1', 'WC-ELE-PCB', 'HAND_SOLDER', ['QUAL-SOLD-AUTH']],
  ['ELE-SOLD-02', 'Precision solder bench 2', 'WC-ELE-PCB', 'HAND_SOLDER', ['QUAL-SOLD-AUTH']],
  ['ELE-HARN-01', 'Harness crimp and pull-test cell', 'WC-ELE-HARN', 'HARNESS', ['QUAL-IPC-A620']],
  ['ELE-PROG-01', 'Board programming station', 'WC-ELE-PCB', 'PROGRAM', ['QUAL-ESD-SAFE']],
  ['SFT-CUT-01', 'Rotary spool cutter', 'WC-SFT', 'SPOOL_CUT', ['QUAL-MAT-KIT']],
  ['SFT-SEW-01', 'Industrial stitcher', 'WC-SFT', 'SEW', ['QUAL-MAT-KIT']],
  ['SFT-WRAP-01', 'Sisal post winder', 'WC-SFT', 'POST_WRAP', ['QUAL-MAT-KIT']],
  ['ASM-01', 'Wand and launcher line', 'WC-ASM-STD', 'ASSEMBLE', ['QUAL-ASM-TORQUE']],
  ['ASM-02', 'Laser and box line', 'WC-ASM-STD', 'ASSEMBLE', ['QUAL-ASM-TORQUE']],
  ['ASM-03', 'Heavy tower rig', 'WC-ASM-HVY', 'ASSEMBLE_HEAVY', ['QUAL-ASM-HVY']],
  ['QAL-CMM-01', 'Coordinate measuring machine', 'WC-QAL', 'INSPECT', ['QUAL-QAL-INSP']],
  ['TST-MECH-01', 'Cycle and drop rig', 'WC-TST', 'TEST_MECH', ['QUAL-SITE-SAFE']],
  ['TST-EL-01', 'Energized electrical and laser bench', 'WC-TST', 'TEST_ELECTRICAL', ['QUAL-ELEC-TEST-SAFE']],
  ['TST-REWORK-01', 'Electronics rework bench', 'WC-TST', 'REWORK', ['QUAL-SOLD-AUTH']],
  ['PKG-LINE-01', 'Carton erect and weigh line', 'WC-PKG', 'PACK', ['QUAL-MAT-KIT']],
  ['PKG-LABEL-01', 'Pack label station', 'WC-PKG', 'LABEL', ['QUAL-MAT-KIT']],
].map(([code, name, workCenterCode, activityCode, operatorQualifications]) => ({ code, name, workCenterCode, activityCode, operatorQualifications }));

// Setup profiles are separate operations on the existing mills, not additional machines.
export const machineActivities = [
  ...machines.map(machine => ({ machineCode: machine.code, activityCode: machine.activityCode, operatorQualifications: machine.operatorQualifications })),
  { machineCode: 'CNC-01', activityCode: 'CNC_SETUP', operatorQualifications: ['QUAL-CNC-SETUP'] },
  { machineCode: 'CNC-02', activityCode: 'CNC_SETUP', operatorQualifications: ['QUAL-CNC-SETUP'] },
];

export const nonMachineStations = [
  ['RCV-DOCK-01', 'Receiving dock door 1', 'WC-RCV'], ['RCV-DOCK-02', 'Receiving dock door 2', 'WC-RCV'],
  ['RAW-RACK-A', 'Controlled raw-material rack A', 'WC-RAW'], ['RAW-SPOOL-CRIB', 'Spool and roll crib', 'WC-RAW'],
  ['KIT-LANE-01', 'Kitting lane 1', 'WC-KIT'], ['KIT-LANE-02', 'Kitting lane 2', 'WC-KIT'],
  ['QAL-BENCH-01', 'Inspection bench', 'WC-QAL'], ['QAL-MRB-CAGE', 'MRB quarantine cage', 'WC-QAL'],
  ['SHP-DOCK-01', 'Outbound dock', 'WC-SHP'], ['SHP-PALLET-01', 'Shipping pallet staging', 'WC-SHP'],
  ['MNT-CRIB-01', 'Tool and calibration crib', 'WC-MNT'], ['MNT-BAY-01', 'Maintenance bay', 'WC-MNT'],
  ['FAC-AIR-01', 'Compressed air utility', 'WC-MNT'], ['FAC-ESD-MON', 'Plant ESD / power monitor', 'WC-ELE-PCB'],
].map(([code, name, workCenterCode]) => ({
  code, name, workCenterCode,
  type: code === 'FAC-AIR-01' ? 'Utility'
    : code === 'FAC-ESD-MON' ? 'TestEquipment'
    : code.startsWith('RCV-DOCK') || code.startsWith('SHP-DOCK') || code.endsWith('BAY-01') ? 'Building'
        : code.startsWith('MNT-CRIB') ? 'Tooling' : 'Other',
}));

export const activities = [
  ['RECEIVE', 'Receive and stage material'], ['STOCK', 'Store and move raw material'], ['CNC_MILL', 'CNC mill operation'],
  ['CNC_LATHE', 'CNC lathe operation'], ['CNC_SETUP', 'CNC setup and programming'], ['FAB_CUT', 'Fabrication cutting'],
  ['FAB_FORM', 'Press-brake forming'], ['FAB_JOIN', 'Heavy frame joining'], ['FAB_DEBURR', 'Deburring'],
  ['PCB_ASSEMBLE', 'PCB assembly and visual verification'], ['HAND_SOLDER', 'Hand soldering and rework'],
  ['HARNESS', 'Cable and wire harness assembly'], ['PROGRAM', 'Electronics programming'], ['SPOOL_CUT', 'Spool and roll cutting'],
  ['SEW', 'Soft-goods stitching'], ['POST_WRAP', 'Sisal post winding'], ['KIT_VERIFY', 'Kit verification and staging'],
  ['ASSEMBLE', 'Standard assembly'], ['ASSEMBLE_HEAVY', 'Heavy assembly'], ['INSPECT', 'Metrology and inspection'],
  ['MRB_REVIEW', 'Material review board'], ['TEST_MECH', 'Mechanical functional test'],
  ['TEST_ELECTRICAL', 'Electrical functional test'], ['REWORK', 'Electronics rework'], ['PACK', 'Pack and weigh'],
  ['LABEL', 'Packaging label verification'], ['SHIP', 'Finished goods shipping'], ['TOOLING', 'Tool and gauge control'],
  ['MAINTENANCE', 'Maintenance and return to service'],
].map(([code, name]) => ({ code, name }));

const workCenterActivityEntries = [
  ...machineActivities.flatMap(profile => {
    const machine = machines.find(item => item.code === profile.machineCode);
    return [[machine.workCenterCode, profile.activityCode]];
  }),
  ['WC-RCV', 'RECEIVE'], ['WC-RAW', 'STOCK'], ['WC-KIT', 'KIT_VERIFY'], ['WC-SHP', 'SHIP'],
  ['WC-MNT', 'TOOLING'], ['WC-MNT', 'MAINTENANCE'],
];
export const workCenterActivities = [...new Map(workCenterActivityEntries
  .map(([workCenterCode, activityCode]) => [`${workCenterCode}:${activityCode}`, { workCenterCode, activityCode }])).values()];

// The brief says 18 qualification types, but its detailed matrix names 23 including trainer
// coverage and the Toolpath / First-Article CNC setup prerequisite. Keep every named gate.
export const qualifications = [
  ['QUAL-SITE-SAFE', 'Site orientation, HazCom, PPE and stop-work', 'Safety', []],
  ['QUAL-ESD-SAFE', 'Electrostatic discharge controls', 'Safety', ['QUAL-SITE-SAFE']],
  ['QUAL-PIT-FORKLIFT', 'Powered industrial truck evaluation', 'Safety', ['QUAL-SITE-SAFE']],
  ['QUAL-MAT-KIT', 'Material handling and kit verification', 'Process', ['QUAL-SITE-SAFE']],
  ['QUAL-CNC-MILL', 'CNC mill operation', 'Machine', ['QUAL-SITE-SAFE']],
  ['QUAL-CNC-LATHE', 'CNC lathe operation', 'Machine', ['QUAL-SITE-SAFE']],
  ['QUAL-CNC-TOOLPATH', 'Toolpath and first-article verification', 'SpecialProcess', ['QUAL-SITE-SAFE']],
  ['QUAL-CNC-SETUP', 'CNC setup and programming authorization', 'SpecialProcess', ['QUAL-CNC-TOOLPATH'], [
    { groupCode: 'MILL-OR-LATHE', name: 'CNC mill or lathe authorization', qualificationCodes: ['QUAL-CNC-MILL', 'QUAL-CNC-LATHE'] },
  ]],
  ['QUAL-TRAINER-CNC', 'CNC trainer authorization', 'Trainer', ['QUAL-CNC-LATHE']],
  ['QUAL-FAB-CUT', 'Fabrication cutting and forming', 'Machine', ['QUAL-SITE-SAFE']],
  ['QUAL-FAB-JOIN', 'Heavy frame joining', 'SpecialProcess', ['QUAL-SITE-SAFE']],
  ['QUAL-IPC-A610', 'IPC-A-610H electronic assembly acceptability', 'SpecialProcess', ['QUAL-ESD-SAFE']],
  ['QUAL-IPC-JSTD001', 'IPC J-STD-001H soldered assemblies', 'SpecialProcess', ['QUAL-SITE-SAFE']],
  ['QUAL-SOLD-AUTH', 'Observed practical soldering authorization', 'SpecialProcess', ['QUAL-IPC-JSTD001']],
  ['QUAL-IPC-A620', 'IPC/WHMA-A-620E cable and harness assemblies', 'SpecialProcess', ['QUAL-SITE-SAFE']],
  ['QUAL-QAL-INSP', 'Independent inspection and lot genealogy', 'Process', ['QUAL-SITE-SAFE']],
  ['QUAL-INSP-ELEC', 'Independent electronics inspector authorization', 'SpecialProcess', ['QUAL-IPC-A610', 'QUAL-IPC-A620', 'QUAL-QAL-INSP']],
  ['QUAL-MRB-DISP', 'Material review board disposition authority', 'SpecialProcess', ['QUAL-QAL-INSP']],
  ['QUAL-ELEC-TEST-SAFE', 'Energized electrical test safety', 'Safety', ['QUAL-SITE-SAFE']],
  ['QUAL-ASM-TORQUE', 'Calibrated torque tool and assembly safety', 'Process', ['QUAL-SITE-SAFE']],
  ['QUAL-ASM-HVY', 'Heavy assembly and team-lift qualification', 'SpecialProcess', ['QUAL-ASM-TORQUE']],
  ['QUAL-MNT-LOTO', 'Hazardous-energy control / lockout-tagout', 'Safety', ['QUAL-SITE-SAFE']],
  ['QUAL-MNT-RTS', 'Maintenance return-to-service release', 'SpecialProcess', ['QUAL-MNT-LOTO']],
].map(([code, name, kind, prerequisites, prerequisiteAlternatives = []]) => ({ code, name, kind, prerequisites, prerequisiteAlternatives, validityDays: 365 }));

export const baselinePresenceByCenter = new Map([
  ['WC-RCV', [0, 1]], ['WC-RAW', [0, 1]], ['WC-CNC-MILL', [0, 1]], ['WC-CNC-LATHE', [0, 1]],
  ['WC-FAB', [2, 3]], ['WC-ELE-PCB', [0, 1]], ['WC-ELE-HARN', [0, 1]], ['WC-SFT', [0, 1]],
  ['WC-KIT', [0, 1]], ['WC-ASM-STD', [0, 1]], ['WC-ASM-HVY', [0, 1]], ['WC-QAL', [0, 1]],
  ['WC-TST', [0, 1]], ['WC-PKG', [0, 1]], ['WC-SHP', [0, 1]], ['WC-MNT', [0, 1]],
]);

export const shifts = [
  { code: 'SHIFT-DAY', siteCode: site.code, name: 'Day Shift', start: '06:00:00', durationMinutes: 480, days: [1, 2, 3, 4, 5] },
  { code: 'SHIFT-SWG', siteCode: site.code, name: 'Swing Shift', start: '14:00:00', durationMinutes: 480, days: [1, 2, 3, 4, 5] },
  { code: 'SHIFT-WKD', siteCode: site.code, name: 'Weekend / Overlap', start: '06:00:00', durationMinutes: 720, days: [6, 7] },
];

// S05 uses an individual, early qualification expiry to exercise upcoming coverage risk without
// shortening the global CNC-mill policy or changing other operators' records.
export const scenarioQualificationExpiry = { machineCode: 'CNC-02', qualificationCode: 'QUAL-CNC-MILL', daysFromSeed: 5 };

const rosterPlans = [
  ['WC-RCV', 3, 'Receiving Associate', 'RECEIVE', [null], ['QUAL-PIT-FORKLIFT']],
  ['WC-RAW', 3, 'Raw Material Handler', 'STOCK', [null], ['QUAL-MAT-KIT']],
  ['WC-CNC-MILL', 4, 'CNC Mill Operator', 'CNC_MILL', ['CNC-01', 'CNC-02'], ['QUAL-CNC-MILL']],
  ['WC-CNC-LATHE', 3, 'CNC Lathe Operator', 'CNC_LATHE', ['CNC-03'], ['QUAL-CNC-LATHE']],
  ['WC-FAB', 4, 'Fabrication Technician', 'FAB_CUT', ['FAB-SAW-01', 'FAB-BRAKE-01', 'FAB-WELD-01', 'FAB-WELD-01'], ['QUAL-FAB-CUT']],
  ['WC-ELE-PCB', 5, 'Electronics Assembly Technician', 'PCB_ASSEMBLE', ['ELE-SMT-01', 'ELE-SOLD-01', 'ELE-SOLD-02', 'ELE-PROG-01', 'ELE-SMT-01'], ['QUAL-ESD-SAFE', 'QUAL-IPC-A610']],
  ['WC-ELE-HARN', 3, 'Harness Assembly Technician', 'HARNESS', ['ELE-HARN-01'], ['QUAL-IPC-A620']],
  ['WC-SFT', 4, 'Soft Goods Operator', 'SPOOL_CUT', ['SFT-CUT-01', 'SFT-SEW-01', 'SFT-WRAP-01', 'SFT-CUT-01'], ['QUAL-MAT-KIT']],
  ['WC-KIT', 3, 'Kitting Technician', 'KIT_VERIFY', [null], ['QUAL-MAT-KIT']],
  ['WC-ASM-STD', 4, 'Assembly Technician', 'ASSEMBLE', ['ASM-01', 'ASM-02'], ['QUAL-ASM-TORQUE']],
  ['WC-ASM-HVY', 4, 'Heavy Assembly Technician', 'ASSEMBLE_HEAVY', ['ASM-03'], ['QUAL-ASM-TORQUE', 'QUAL-ASM-HVY']],
  ['WC-QAL', 3, 'Quality Inspector', 'INSPECT', ['QAL-CMM-01'], ['QUAL-QAL-INSP', 'QUAL-IPC-A610', 'QUAL-IPC-A620', 'QUAL-INSP-ELEC']],
  ['WC-TST', 3, 'Functional Test Technician', 'TEST_MECH', ['TST-MECH-01', 'TST-MECH-01', 'TST-EL-01'], ['QUAL-ELEC-TEST-SAFE']],
  ['WC-PKG', 2, 'Packaging Associate', 'PACK', ['PKG-LINE-01', 'PKG-LABEL-01'], ['QUAL-MAT-KIT']],
  ['WC-SHP', 2, 'Shipping Associate', 'SHIP', [null], ['QUAL-MAT-KIT']],
  ['WC-MNT', 2, 'Maintenance Technician', 'MAINTENANCE', [null], ['QUAL-MNT-LOTO', 'QUAL-MNT-RTS']],
];

const workerNames = [
  'Avery Martin', 'Jordan Ellis', 'Casey Nguyen', 'Riley Brooks', 'Morgan Patel', 'Taylor Bennett', 'Cameron Reed', 'Quinn Hayes',
  'Jamie Flores', 'Drew Sullivan', 'Skyler Price', 'Parker James', 'Rowan Cooper', 'Emerson Diaz', 'Reese Howard', 'Finley Ross',
  'Dakota Perry', 'Alex Morgan', 'Sage Turner', 'Kendall Gray', 'Robin Bailey', 'Charlie Ward', 'Shiloh Evans', 'Marley Foster',
  'Harper Cole', 'Logan Rivera', 'Micah Bell', 'Ari Simmons', 'Noel Murphy', 'Elliot James', 'Rory Bennett', 'Samira Khan',
  'Devon Brooks', 'Kai Mitchell', 'Remy Carter', 'Lee Thompson', 'Ash Morgan', 'Toby Jenkins', 'Nico Alvarez', 'Milan Shah',
  'Jules Porter', 'Indigo Ross', 'River Young', 'Blair Kim', 'Ellis Wright', 'Cleo Bennett', 'Zion Parker', 'Lennox Ward',
  'Mina Patel', 'Ren Davis', 'Cory Nguyen', 'Ira Sullivan',
  'Asha Okafor', 'Noah Campbell', 'Priya Shah', 'Eli Washington',
  'Maya Chen', 'Jonah Brooks', 'Lena Ortiz', 'Theo Jackson',
];

export const workers = [];
let workerIndex = 0;
let offsiteWorkerIndex = 0;
for (const [workCenterCode, count, jobTitle, activityCode, machineCodes, qualificationCodes] of rosterPlans) {
  for (let localIndex = 0; localIndex < count; localIndex++) {
    const index = workerIndex++;
    const presentAtBaseline = (baselinePresenceByCenter.get(workCenterCode) ?? []).includes(localIndex);
    const id = String(index + 1).padStart(3, '0');
    const machineCode = machineCodes[localIndex % machineCodes.length];
    const machine = machines.find(item => item.code === machineCode);
    const extraQualifications = machine?.operatorQualifications ?? [];
    const trainerQualifications = workCenterCode === 'WC-CNC-LATHE' && localIndex === 0 ? ['QUAL-TRAINER-CNC'] : [];
    const setupQualifications = workCenterCode === 'WC-CNC-MILL' && localIndex === 0 ? ['QUAL-CNC-SETUP'] : [];
    const shiftCode = presentAtBaseline ? 'SHIFT-DAY' : offsiteWorkerIndex++ < 12 ? 'SHIFT-SWG' : 'SHIFT-WKD';
    workers.push({
      code: `WW-EMP-${id}`, subject: `ww-operator-${id}`, displayName: workerNames[index], jobTitle,
      workCenterCode, activityCode: machine?.activityCode ?? activityCode, machineCode,
      shiftCode,
      qualifications: [...new Set([...qualificationCodes, ...extraQualifications, ...trainerQualifications, ...setupQualifications])],
      presentAtBaseline,
    });
  }
}

// Sprint 10 extends the Sprint 9 roster without renumbering or changing its original 52 identities.
const sprint10Roster = [
  ['WC-RCV', 'Receiving Associate', 'RECEIVE', null, ['QUAL-PIT-FORKLIFT']],
  ['WC-RAW', 'Raw Material Handler', 'STOCK', null, ['QUAL-MAT-KIT']],
  ['WC-CNC-MILL', 'CNC Mill Operator', 'CNC_MILL', 'CNC-02', ['QUAL-CNC-MILL', 'QUAL-CNC-SETUP']],
  ['WC-CNC-LATHE', 'CNC Lathe Trainer', 'CNC_LATHE', 'CNC-03', ['QUAL-CNC-LATHE', 'QUAL-TRAINER-CNC']],
  ['WC-ELE-PCB', 'Electronics Assembly Technician', 'ELEC_SOLDER', 'ELE-SOLD-02', ['QUAL-ESD-SAFE', 'QUAL-IPC-A610']],
  ['WC-ELE-HARN', 'Harness Assembly Technician', 'HARNESS', 'ELE-HARN-01', ['QUAL-IPC-A620']],
  ['WC-ASM-HVY', 'Heavy Assembly Technician', 'ASSEMBLE_HEAVY', 'ASM-03', ['QUAL-ASM-TORQUE', 'QUAL-ASM-HVY']],
  ['WC-QAL', 'Quality Inspector', 'INSPECT', 'QAL-CMM-01', ['QUAL-QAL-INSP', 'QUAL-IPC-A610', 'QUAL-IPC-A620', 'QUAL-INSP-ELEC']],
];
for (let index = 0; index < sprint10Roster.length; index++) {
  const [workCenterCode, jobTitle, activityCode, machineCode, qualificationCodes] = sprint10Roster[index];
  const id = String(workerIndex + 1).padStart(3, '0');
  const machine = machineCode ? machines.find(item => item.code === machineCode) : null;
  workers.push({
    code: `WW-EMP-${id}`, subject: `ww-operator-${id}`, displayName: workerNames[workerIndex], jobTitle,
    workCenterCode, activityCode: machine?.activityCode ?? activityCode, machineCode,
    shiftCode: index < 4 ? 'SHIFT-SWG' : 'SHIFT-WKD',
    qualifications: [...new Set([...qualificationCodes, ...(machine?.operatorQualifications ?? [])])],
    presentAtBaseline: false,
  });
  workerIndex++;
}

export const demoPrincipals = [
  { subject: 'ww-demo-engineer', displayName: 'WhiskerWorks Demo Engineer', roles: ['ManufacturingEngineer', 'TrainingCoordinator', 'SafetyEngineer', 'QualityEngineer', 'MaintenancePlanner', 'Buyer', 'MaterialHandler', 'DocumentController'] },
  { subject: 'ww-demo-certifier', displayName: 'WhiskerWorks Demo Certifier', roles: ['Certifier', 'SafetyApprover', 'EngineeringApprover', 'PurchasingManager'] },
  { subject: 'ww-demo-supervisor', displayName: 'WhiskerWorks Demo Supervisor', roles: ['Supervisor'] },
  { subject: 'ww-demo-planner', displayName: 'WhiskerWorks Demo Planner', roles: ['Planner', 'ProgramManager'] },
  { subject: 'ww-demo-inventory', displayName: 'WhiskerWorks Demo Inventory Controller', roles: ['InventoryController'] },
  { subject: 'ww-demo-attendance-importer', displayName: 'WhiskerWorks Demo Attendance Importer', roles: ['AttendanceImporter'] },
  // A second access manager: nobody grants their own program access, including the seeding administrator.
  { subject: 'ww-demo-security', displayName: 'WhiskerWorks Demo Security Administrator', roles: ['SecurityAdministrator'] },
];

export const productFamilies = [
  ['WW-CAT-1000', 'Titan Feather Wand'], ['WW-CAT-2000', 'Turbo Laser Chase Arena'], ['WW-CAT-3000', 'Mega Yarn Launcher'],
  ['WW-CAT-4000', 'Sky-Scratcher Tower'], ['WW-CAT-5000', 'Auto-Box Deployment Station'],
].map(([partNumber, description]) => ({ partNumber, description }));

export const targets = { zones: 15, workCenters: 16, machines: 24, workers: 60, productFamilies: 5 };
