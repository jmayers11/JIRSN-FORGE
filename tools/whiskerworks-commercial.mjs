// Fictional commercial and engineering catalog for the isolated WhiskerWorks demo.
// Names and specifications are invented for product demonstration; they are not approved manufacturing instructions.
export { scenarioCatalog } from '../src/Forge.Host/wwwroot/js/data/whiskerworks-scenarios.js';
const raw = (partNumber, description, unitOfMeasure = 'EA', leadTimeDays = 7, lotTracked = true) => ({
  partNumber, description, unitOfMeasure, makeBuy: 'Buy', class: 'RawMaterial', leadTimeDays, lotTracked, serialized: false,
});
const bought = (partNumber, description, unitOfMeasure = 'EA', leadTimeDays = 10, itemClass = 'Cots') => ({
  partNumber, description, unitOfMeasure, makeBuy: 'Buy', class: itemClass, leadTimeDays, lotTracked: true, serialized: false,
});
const made = (partNumber, description, unitOfMeasure = 'EA') => ({
  partNumber, description, unitOfMeasure, makeBuy: 'Make', class: 'Make', leadTimeDays: 2, lotTracked: true, serialized: false,
});

// Five fictional families, each with 19 purchased or manufactured components (95 total).
export const components = [
  // Metals, polymer and soft-goods source materials (19)
  raw('WW-ALU-BAR-12', '6061-T6 aluminum bar, 12 mm', 'M', 12), raw('WW-ALU-TUBE-06', '6061-T6 telescoping tube, 6 mm', 'M', 12),
  raw('WW-DEL-RING-STK', 'Acetal ring-track stock', 'KG', 9), raw('WW-STEEL-PLATE-08', 'Low-carbon steel plate, 8 mm', 'KG', 14),
  bought('WW-ALU-FERRULE', 'Anodized wand ferrule'), bought('WW-PLUME-XL', 'Synthetic plume cluster'),
  raw('WW-ROPE-KEV-500M', 'Kevlar blend tether cord, 500 m spool', 'M', 14), raw('WW-YARN-BULK-1000M', 'Braided plush yarn, 1000 m cone', 'M', 10),
  raw('WW-SISAL-SPOOL-250M', 'Natural sisal rope, 250 m master spool', 'M', 12), raw('WW-FAB-PLUSH-ROLL', 'Fictional plush upholstery roll', 'M2', 12),
  raw('WW-ABS-PELLET-BLK', 'Black ABS molding pellets', 'KG', 8), raw('WW-POLY-CORD-100M', 'Polyester reinforcement cord, 100 m', 'M', 8),
  raw('WW-FOAM-SHEET', 'Closed-cell foam sheet', 'M2', 8), raw('WW-BOX-BOARD-5PLY', 'Five-ply carton board', 'SHEET', 8),
  bought('WW-ADHESIVE-IND', 'Water-based assembly adhesive', 'L', 7, 'Consumable'), bought('WW-PAINT-POWDER', 'Powder coat, charcoal', 'KG', 10, 'Consumable'),
  bought('WW-SHRINK-TUBE', 'Heat-shrink tubing assortment', 'M', 8, 'Consumable'), bought('WW-LABEL-STOCK', 'Thermal label stock', 'ROLL', 6, 'Packaging'),
  bought('WW-LUBE-CUT', 'Water-soluble machining coolant', 'L', 7, 'Consumable'),

  // Motors, controls, sensors, connectors and electronics (19)
  bought('WW-MOT-24V-DC', '24 V brushed DC gearmotor', 'EA', 18), bought('WW-LAS-MOD-01', 'Low-power visible diode module'),
  made('WW-PCB-CTRL-200', 'Laser arena controller PCB'), made('WW-PCB-CTRL-300', 'Yarn launcher timer PCB'),
  bought('WW-SENS-PROX-05', 'Short-range proximity sensor array'), bought('WW-HARN-200', 'Laser arena wire harness'),
  bought('WW-HARN-500', 'Auto-box actuator harness'), bought('WW-SW-ESTOP-01', 'Latching emergency-stop switch'),
  bought('WW-PSU-24V-60W', '24 V enclosed power supply'), bought('WW-BATT-18650-2S', 'Protected two-cell battery pack'),
  bought('WW-USB-C-BOARD', 'USB-C charging daughterboard'), bought('WW-IC-TIMER-08', 'Programmable timer IC'),
  bought('WW-CAP-470UF', '470 µF electrolytic capacitor'), bought('WW-CONN-JST-04', 'Four-position wire connector'),
  bought('WW-WIRE-18AWG', '18 AWG stranded wire', 'M', 8, 'RawMaterial'), bought('WW-CRIMP-TERM-01', 'Insulated crimp terminal'),
  bought('WW-FUSE-2A', '2 A inline fuse'), bought('WW-LED-STATUS-03', 'Three-color status LED'),
  bought('WW-ENCODER-ROT-01', 'Rotary position encoder'),

  // Mechanical components, fasteners and bearings (19)
  bought('WW-BRG-608-2RS', 'Shielded 608 bearing'), bought('WW-GEAR-POM-20T', 'Acetal spur gear, 20 tooth'),
  bought('WW-SHAFT-SS-08', 'Stainless shaft, 8 mm'), bought('WW-WHEEL-CAST-50', 'Swivel caster, 50 mm'),
  bought('WW-SCREW-M4-12', 'M4 × 12 mm machine screw', 'EA', 5, 'StandardHardware'), bought('WW-NUT-M4-NYL', 'M4 nylon locknut', 'EA', 5, 'StandardHardware'),
  bought('WW-WASHER-M4', 'M4 flat washer', 'EA', 5, 'StandardHardware'), bought('WW-BOLT-M8-30', 'M8 × 30 mm structural bolt', 'EA', 6, 'StandardHardware'),
  bought('WW-INSERT-M6', 'M6 threaded insert', 'EA', 7, 'StandardHardware'), bought('WW-SPRING-EXT-30', 'Extension spring, 30 mm'),
  bought('WW-ROLLER-DEL-20', 'Acetal guide roller'), bought('WW-HINGE-SS-40', 'Stainless hinge, 40 mm'),
  bought('WW-LATCH-BOX-01', 'Enclosure latch'), bought('WW-FOOT-RUB-25', 'Anti-slip rubber foot, 25 mm'),
  bought('WW-PIN-DOWEL-06', 'Hardened dowel pin, 6 mm'), bought('WW-CLIP-RETAIN-08', 'Retaining clip, 8 mm'),
  bought('WW-GROMMET-12', 'Cable grommet, 12 mm'), bought('WW-RAIL-AL-300', 'Aluminum guide rail, 300 mm'),
  bought('WW-BELT-TIMING-05', 'Timing belt, 5 mm pitch'),

  // Made subassemblies (19)
  made('WW-WAND-SEG-ASSY', 'Telescoping wand segment set'), made('WW-WAND-ASSY', 'Titan feather wand assembly'),
  made('WW-TRACK-HOUSING', 'Acetal arena track housing'), made('WW-LASER-CARRIER', 'Laser module carrier'),
  made('WW-HARN-200-ASSY', 'Verified laser arena harness'), made('WW-ARENA-DRIVE', 'Arena gearmotor drive module'),
  made('WW-FLYWHEEL-ASSY', 'Yarn launcher flywheel module'), made('WW-LAUNCH-DRUM', 'Braided yarn launch drum'),
  made('WW-CTRL-300-ASSY', 'Programmed launcher controller'), made('WW-TOWER-BASE-ASSY', 'Tower structural base assembly'),
  made('WW-TOWER-FRAME-ASSY', 'Joined tower frame assembly'), made('WW-SISAL-COLUMN', 'Wrapped sisal tower column'),
  made('WW-PERCH-PAD-ASSY', 'Soft perch pad assembly'), made('WW-CHUTE-ASSY', 'Formed deployment chute'),
  made('WW-SENSOR-ARRAY-ASSY', 'Mounted proximity sensor array'), made('WW-HARN-500-ASSY', 'Verified deployment harness'),
  made('WW-ACTUATOR-ASSY', 'Box release actuator assembly'), made('WW-PACK-CAT-01', 'Protective cat-toy carton kit', 'EA'),
  made('WW-FAB-BRACKET-01', 'Formed equipment mounting bracket'),

  // Packaging and controlled consumables (19)
  bought('WW-CARTON-1000', 'Retail carton, Titan Feather Wand', 'EA', 8, 'Packaging'), bought('WW-CARTON-2000', 'Retail carton, Turbo Laser Arena', 'EA', 8, 'Packaging'),
  bought('WW-CARTON-3000', 'Retail carton, Mega Yarn Launcher', 'EA', 8, 'Packaging'), bought('WW-CARTON-4000', 'Retail carton, Sky-Scratcher Tower', 'EA', 12, 'Packaging'),
  bought('WW-CARTON-5000', 'Retail carton, Auto-Box Station', 'EA', 8, 'Packaging'), bought('WW-INSERT-FOAM-01', 'Molded protective carton insert', 'EA', 8, 'Packaging'),
  bought('WW-BAG-PE-01', 'Clear protective bag', 'EA', 5, 'Packaging'), bought('WW-TAPE-PAPER-01', 'Paper carton sealing tape', 'M', 5, 'Packaging'),
  bought('WW-INK-THERM-01', 'Thermal transfer ribbon', 'ROLL', 6, 'Consumable'), bought('WW-THREAD-POLY-01', 'Upholstery thread cone', 'EA', 7, 'Consumable'),
  bought('WW-NEEDLE-IND-01', 'Industrial stitching needle', 'EA', 6, 'Consumable'), bought('WW-DISC-ABRASIVE-01', 'Abrasive finishing disc', 'EA', 5, 'Consumable'),
  bought('WW-WELD-WIRE-01', 'Fictional mild-steel weld wire', 'KG', 8, 'Consumable'), bought('WW-GAS-SHIELD-01', 'Shielding gas cylinder service', 'EA', 9, 'Service'),
  bought('WW-CLEANER-ESD-01', 'ESD-safe bench cleaner', 'L', 6, 'Consumable'), bought('WW-TIE-CABLE-100', 'Cable tie, 100 mm', 'EA', 5, 'StandardHardware'),
  bought('WW-PAD-FELT-01', 'Felt contact pad', 'EA', 6, 'Consumable'), bought('WW-PAPER-TRACE-01', 'Fictional traceability insert', 'EA', 5, 'Packaging'),
  bought('WW-PALLET-STD-01', 'Reusable shipping pallet', 'EA', 8, 'Packaging'),
];

export const products = [
  { partNumber: 'WW-CAT-1000', description: 'Titan Feather Wand', bom: [
    ['WW-WAND-ASSY', 1], ['WW-ROPE-KEV-500M', 6, 2], ['WW-PLUME-XL', 1], ['WW-CARTON-1000', 1], ['WW-INSERT-FOAM-01', 1], ['WW-PAPER-TRACE-01', 1],
  ], route: [
    ['Turn wand ferrule seat', 'WC-CNC-LATHE', 'CNC_LATHE', ['CNC-03'], ['QUAL-CNC-LATHE'], 1, 5],
    ['Cut tether and fit plume cluster', 'WC-SFT', 'SPOOL_CUT', ['SFT-CUT-01'], ['QUAL-MAT-KIT'], 2, 3],
    ['Assemble telescoping wand', 'WC-ASM-STD', 'ASSEMBLE', ['ASM-01'], ['QUAL-ASM-TORQUE'], 2, 6],
    ['Mechanical function check', 'WC-TST', 'TEST_MECH', ['TST-MECH-01'], [], 1, 2],
    ['Pack and weigh', 'WC-PKG', 'PACK', ['PKG-LINE-01'], ['QUAL-MAT-KIT'], 1, 2],
  ] },
  { partNumber: 'WW-CAT-2000', description: 'Turbo Laser Chase Arena', bom: [
    ['WW-TRACK-HOUSING', 1], ['WW-ARENA-DRIVE', 1], ['WW-LASER-CARRIER', 1], ['WW-PCB-CTRL-200', 1], ['WW-HARN-200-ASSY', 1],
    ['WW-MOT-24V-DC', 1], ['WW-LAS-MOD-01', 1], ['WW-CARTON-2000', 1], ['WW-PAPER-TRACE-01', 1],
  ], route: [
    ['Machine ring-track housing', 'WC-CNC-MILL', 'CNC_MILL', ['CNC-01', 'CNC-02'], ['QUAL-CNC-MILL'], 1, 12],
    ['Populate and inspect controller', 'WC-ELE-PCB', 'PCB_ASSEMBLE', ['ELE-SMT-01'], ['QUAL-ESD-SAFE', 'QUAL-IPC-A610'], 1, 9],
    ['Verify harness crimp and continuity', 'WC-ELE-HARN', 'HARNESS', ['ELE-HARN-01'], ['QUAL-IPC-A620'], 1, 7],
    ['Assemble arena', 'WC-ASM-STD', 'ASSEMBLE', ['ASM-02'], ['QUAL-ASM-TORQUE'], 2, 14],
    ['Electrical and motion function test', 'WC-TST', 'TEST_ELECTRICAL', ['TST-EL-01'], ['QUAL-ELEC-TEST-SAFE'], 1, 8],
    ['Pack and label', 'WC-PKG', 'PACK', ['PKG-LINE-01'], ['QUAL-MAT-KIT'], 1, 3],
  ] },
  { partNumber: 'WW-CAT-3000', description: 'Mega Yarn Launcher', bom: [
    ['WW-FLYWHEEL-ASSY', 1], ['WW-LAUNCH-DRUM', 1], ['WW-CTRL-300-ASSY', 1], ['WW-MOT-24V-DC', 2],
    ['WW-YARN-BULK-1000M', 12, 1], ['WW-CARTON-3000', 1], ['WW-PAPER-TRACE-01', 1],
  ], route: [
    ['Machine flywheel housing', 'WC-CNC-MILL', 'CNC_MILL', ['CNC-01', 'CNC-02'], ['QUAL-CNC-MILL'], 1, 10],
    ['Program and inspect controller', 'WC-ELE-PCB', 'PROGRAM', ['ELE-PROG-01'], ['QUAL-ESD-SAFE'], 1, 6],
    ['Cut yarn charge', 'WC-SFT', 'SPOOL_CUT', ['SFT-CUT-01'], ['QUAL-MAT-KIT'], 1, 3],
    ['Assemble launcher', 'WC-ASM-STD', 'ASSEMBLE', ['ASM-01'], ['QUAL-ASM-TORQUE'], 2, 13],
    ['Mechanical cycle test', 'WC-TST', 'TEST_MECH', ['TST-MECH-01'], [], 1, 7],
    ['Pack and label', 'WC-PKG', 'PACK', ['PKG-LINE-01'], ['QUAL-MAT-KIT'], 1, 3],
  ] },
  { partNumber: 'WW-CAT-4000', description: 'Sky-Scratcher Tower', bom: [
    ['WW-TOWER-BASE-ASSY', 1], ['WW-TOWER-FRAME-ASSY', 1], ['WW-SISAL-COLUMN', 3], ['WW-PERCH-PAD-ASSY', 2],
    ['WW-CARTON-4000', 1], ['WW-PALLET-STD-01', 1], ['WW-PAPER-TRACE-01', 1],
  ], route: [
    ['Cut structural frame stock', 'WC-FAB', 'FAB_CUT', ['FAB-SAW-01'], ['QUAL-FAB-CUT'], 1, 8],
    ['Join frame under two-person rule', 'WC-FAB', 'FAB_JOIN', ['FAB-WELD-01'], ['QUAL-FAB-JOIN'], 2, 16],
    ['Wrap sisal columns', 'WC-SFT', 'POST_WRAP', ['SFT-WRAP-01'], ['QUAL-MAT-KIT'], 1, 12],
    ['Assemble tower under two-person rule', 'WC-ASM-HVY', 'ASSEMBLE_HEAVY', ['ASM-03'], ['QUAL-ASM-HVY'], 2, 22],
    ['Independent dimensional inspection', 'WC-QAL', 'INSPECT', ['QAL-CMM-01'], ['QUAL-QAL-INSP'], 1, 8],
    ['Pack and palletize', 'WC-PKG', 'PACK', ['PKG-LINE-01'], ['QUAL-MAT-KIT'], 2, 8],
  ] },
  { partNumber: 'WW-CAT-5000', description: 'Auto-Box Deployment Station', bom: [
    ['WW-CHUTE-ASSY', 1], ['WW-SENSOR-ARRAY-ASSY', 1], ['WW-HARN-500-ASSY', 1], ['WW-ACTUATOR-ASSY', 1],
    ['WW-PCB-CTRL-200', 1], ['WW-CARTON-5000', 1], ['WW-PAPER-TRACE-01', 1],
  ], route: [
    ['Form chute and brackets', 'WC-FAB', 'FAB_FORM', ['FAB-BRAKE-01'], ['QUAL-FAB-CUT'], 1, 11],
    ['Build and verify actuator harness', 'WC-ELE-HARN', 'HARNESS', ['ELE-HARN-01'], ['QUAL-IPC-A620'], 1, 8],
    ['Assemble deployment station', 'WC-ASM-STD', 'ASSEMBLE', ['ASM-02'], ['QUAL-ASM-TORQUE'], 2, 12],
    ['Electrical function and interlock check', 'WC-TST', 'TEST_ELECTRICAL', ['TST-EL-01'], ['QUAL-ELEC-TEST-SAFE'], 1, 9],
    ['Pack and label', 'WC-PKG', 'PACK', ['PKG-LINE-01'], ['QUAL-MAT-KIT'], 1, 3],
  ] },
];

export const subassemblyBoms = [
  ['WW-WAND-SEG-ASSY', [['WW-ALU-BAR-12', 1.2], ['WW-ALU-TUBE-06', 0.8], ['WW-ALU-FERRULE', 2], ['WW-SCREW-M4-12', 4]]],
  ['WW-WAND-ASSY', [['WW-WAND-SEG-ASSY', 1], ['WW-GROMMET-12', 2], ['WW-FOOT-RUB-25', 1]]],
  ['WW-TRACK-HOUSING', [['WW-DEL-RING-STK', 2.5], ['WW-ROLLER-DEL-20', 4], ['WW-PIN-DOWEL-06', 4]]],
  ['WW-LASER-CARRIER', [['WW-ABS-PELLET-BLK', 0.2], ['WW-SCREW-M4-12', 2]]],
  ['WW-HARN-200-ASSY', [['WW-HARN-200', 1], ['WW-TIE-CABLE-100', 4], ['WW-SHRINK-TUBE', 0.3]]],
  ['WW-ARENA-DRIVE', [['WW-MOT-24V-DC', 1], ['WW-GEAR-POM-20T', 1], ['WW-BRG-608-2RS', 2], ['WW-SHAFT-SS-08', 1]]],
  ['WW-FLYWHEEL-ASSY', [['WW-DEL-RING-STK', 1.4], ['WW-BRG-608-2RS', 2], ['WW-SHAFT-SS-08', 1], ['WW-CLIP-RETAIN-08', 2]]],
  ['WW-LAUNCH-DRUM', [['WW-ABS-PELLET-BLK', 0.5], ['WW-YARN-BULK-1000M', 2, 1], ['WW-SPRING-EXT-30', 1]]],
  ['WW-CTRL-300-ASSY', [['WW-PCB-CTRL-300', 1], ['WW-IC-TIMER-08', 1], ['WW-CAP-470UF', 2], ['WW-LED-STATUS-03', 1]]],
  ['WW-TOWER-BASE-ASSY', [['WW-STEEL-PLATE-08', 8], ['WW-WHEEL-CAST-50', 4], ['WW-BOLT-M8-30', 8], ['WW-PAINT-POWDER', 0.5]]],
  ['WW-TOWER-FRAME-ASSY', [['WW-STEEL-PLATE-08', 12], ['WW-WELD-WIRE-01', 0.6], ['WW-GAS-SHIELD-01', 0.1], ['WW-DISC-ABRASIVE-01', 2]]],
  ['WW-SISAL-COLUMN', [['WW-SISAL-SPOOL-250M', 2.5, 2], ['WW-ALU-TUBE-06', 1], ['WW-ADHESIVE-IND', 0.15]]],
  ['WW-PERCH-PAD-ASSY', [['WW-FAB-PLUSH-ROLL', 0.6], ['WW-FOAM-SHEET', 0.4], ['WW-THREAD-POLY-01', 0.08], ['WW-NEEDLE-IND-01', 1]]],
  ['WW-CHUTE-ASSY', [['WW-STEEL-PLATE-08', 3], ['WW-INSERT-M6', 4], ['WW-HINGE-SS-40', 2], ['WW-LATCH-BOX-01', 1]]],
  ['WW-SENSOR-ARRAY-ASSY', [['WW-SENS-PROX-05', 2], ['WW-FAB-BRACKET-01', 2], ['WW-WIRE-18AWG', 1]]],
  ['WW-HARN-500-ASSY', [['WW-HARN-500', 1], ['WW-CRIMP-TERM-01', 12], ['WW-TIE-CABLE-100', 5]]],
  ['WW-ACTUATOR-ASSY', [['WW-MOT-24V-DC', 1], ['WW-BELT-TIMING-05', 1], ['WW-RAIL-AL-300', 1], ['WW-SW-ESTOP-01', 1]]],
  ['WW-PACK-CAT-01', [['WW-BOX-BOARD-5PLY', 1], ['WW-INSERT-FOAM-01', 1], ['WW-BAG-PE-01', 1], ['WW-TAPE-PAPER-01', 1]]],
  ['WW-FAB-BRACKET-01', [['WW-STEEL-PLATE-08', 0.25], ['WW-PAINT-POWDER', 0.05], ['WW-INSERT-M6', 2]]],
  ['WW-PCB-CTRL-200', [['WW-CAP-470UF', 2], ['WW-CONN-JST-04', 3], ['WW-FUSE-2A', 1], ['WW-LED-STATUS-03', 1]]],
  ['WW-PCB-CTRL-300', [['WW-IC-TIMER-08', 1], ['WW-CAP-470UF', 3], ['WW-CONN-JST-04', 2], ['WW-ENCODER-ROT-01', 1]]],
].map(([partNumber, lines]) => ({ partNumber, lines }));

const routeProfiles = {
  'WC-CNC-MILL': ['CNC_MILL', ['CNC-01', 'CNC-02'], ['QUAL-CNC-MILL']],
  'WC-CNC-LATHE': ['CNC_LATHE', ['CNC-03'], ['QUAL-CNC-LATHE']],
  'WC-FAB': ['FAB_FORM', ['FAB-BRAKE-01'], ['QUAL-FAB-CUT']],
  'WC-ELE-PCB': ['PCB_ASSEMBLE', ['ELE-SMT-01'], ['QUAL-ESD-SAFE', 'QUAL-IPC-A610']],
  'WC-ELE-HARN': ['HARNESS', ['ELE-HARN-01'], ['QUAL-IPC-A620']],
  'WC-SFT': ['SEW', ['SFT-SEW-01'], ['QUAL-MAT-KIT']],
  'WC-ASM-STD': ['ASSEMBLE', ['ASM-01'], ['QUAL-ASM-TORQUE']],
  'WC-ASM-HVY': ['ASSEMBLE_HEAVY', ['ASM-03'], ['QUAL-ASM-HVY']],
  'WC-PKG': ['PACK', ['PKG-LINE-01'], ['QUAL-MAT-KIT']],
};
const routeCenterByPart = {
  'WW-WAND-SEG-ASSY': 'WC-CNC-LATHE', 'WW-WAND-ASSY': 'WC-ASM-STD', 'WW-TRACK-HOUSING': 'WC-CNC-MILL',
  'WW-LASER-CARRIER': 'WC-ASM-STD', 'WW-HARN-200-ASSY': 'WC-ELE-HARN', 'WW-ARENA-DRIVE': 'WC-ASM-STD',
  'WW-FLYWHEEL-ASSY': 'WC-CNC-MILL', 'WW-LAUNCH-DRUM': 'WC-SFT', 'WW-CTRL-300-ASSY': 'WC-ELE-PCB',
  'WW-TOWER-BASE-ASSY': 'WC-FAB', 'WW-TOWER-FRAME-ASSY': 'WC-FAB', 'WW-SISAL-COLUMN': 'WC-SFT',
  'WW-PERCH-PAD-ASSY': 'WC-SFT', 'WW-CHUTE-ASSY': 'WC-FAB', 'WW-SENSOR-ARRAY-ASSY': 'WC-ELE-PCB',
  'WW-HARN-500-ASSY': 'WC-ELE-HARN', 'WW-ACTUATOR-ASSY': 'WC-ASM-STD', 'WW-PACK-CAT-01': 'WC-PKG',
  'WW-FAB-BRACKET-01': 'WC-FAB', 'WW-PCB-CTRL-200': 'WC-ELE-PCB', 'WW-PCB-CTRL-300': 'WC-ELE-PCB',
};
export const subassemblyRoutes = Object.entries(routeCenterByPart).map(([partNumber, workCenterCode]) => {
  const [activityCode, eligibleMachines, requiredQualifications] = routeProfiles[workCenterCode];
  return { partNumber, operation: {
    sequence: 10, description: `Build ${partNumber}`, workCenterCode, activityCode,
    eligibleMachines, requiredQualifications, setupMinutes: 3, runMinutesPerUnit: 2,
    crewSize: activityCode === 'ASSEMBLE_HEAVY' ? 2 : 1,
  } };
});

export const allItems = [...components, ...[
  { partNumber: 'WW-CAT-1000', description: 'Titan Feather Wand' },
  { partNumber: 'WW-CAT-2000', description: 'Turbo Laser Chase Arena' },
  { partNumber: 'WW-CAT-3000', description: 'Mega Yarn Launcher' },
  { partNumber: 'WW-CAT-4000', description: 'Sky-Scratcher Tower' },
  { partNumber: 'WW-CAT-5000', description: 'Auto-Box Deployment Station' },
].map(item => ({ ...made(item.partNumber, item.description), leadTimeDays: 1 }))];

export const suppliers = [
  ['WW-SUP-001', 'Copper Kite Components'], ['WW-SUP-002', 'Moonlit Motion Works'], ['WW-SUP-003', 'Juniper Polymer Supply'],
  ['WW-SUP-004', 'Northstar Textile Mill'], ['WW-SUP-005', 'Tandem Fastener House'], ['WW-SUP-006', 'Bluebird Electrical Parts'],
  ['WW-SUP-007', 'Paper Comet Packaging'], ['WW-SUP-008', 'Harborlight Metals'], ['WW-SUP-009', 'Quiet Current Controls'],
  ['WW-SUP-010', 'Cedar Bench Industrial'],
].map(([code, name]) => ({ code, name, notes: 'Fictional WhiskerWorks demonstration supplier.' }));

export const customers = [
  'Whiskerbrook Toy Atelier', 'Marmalade Moon Mercantile', 'Pouncing Comet Stores', 'Velvet Paw Outfitters', 'Cloudtail Play Company',
  'Copper Bell Pet Goods', 'Night Garden Novelty Co.', 'Fable Purr Supply', 'Crescent Whisker Shops', 'Tumbleleaf Toy House',
].map((name, index) => ({ code: `WW-CUS-${String(index + 1).padStart(3, '0')}`, name }));

export const operationalLocations = [
  ['WW-RECEIVING', 'Receiving inspection staging', 'Receiving', 'RCV', false, 'WC-RCV'],
  ['WW-RAW', 'Raw materials warehouse', 'Stores', 'RAW', true, 'WC-RAW'],
  ['WW-INSPECTION', 'Incoming quality inspection', 'Inspection', 'QAL', false, 'WC-QAL'],
  ['WW-QUARANTINE', 'MRB quarantine cage', 'Quarantine', 'QAL', false, 'WC-QAL'],
  ['WW-WIP', 'Controlled work-in-process', 'WipStorage', 'KIT', false, 'WC-KIT'],
  ['WW-STAGING-CNC', 'CNC point-of-use staging', 'Staging', 'CNC', false, 'WC-CNC-MILL'],
  ['WW-STAGING-ELE', 'Electronics point-of-use staging', 'Staging', 'ELE', false, 'WC-ELE-PCB'],
  ['WW-FINISHED', 'Finished goods shipping', 'Shipping', 'SHP', false, 'WC-SHP'],
].map(([code, name, kind, zoneCode, nettable, workCenterCode]) => ({ code, name, kind, zoneCode, nettable, workCenterCode }));

export function createDemoWorkload(asOf) {
  const day = new Date(`${asOf}T12:00:00.000Z`);
  if (Number.isNaN(day.getTime())) throw new Error(`Invalid demo as-of date: ${asOf}`);
  const addDays = offset => new Date(day.getTime() + offset * 86_400_000).toISOString().slice(0, 10);
  const demandLines = Array.from({ length: 28 }, (_, index) => {
    const product = products[index % products.length];
    const customer = customers[(index * 3 + 1) % customers.length];
    return {
      partNumber: product.partNumber, quantity: 8 + (index % 5) * 4, needDate: addDays(5 + (index % 18)), priority: 2 + index % 6,
      kind: 'CustomerOrder', customer: customer.name, reference: `${customer.code}-SO-${String(index + 1).padStart(4, '0')}`,
      sourceKey: `ww-demo:DEMAND-${String(index + 1).padStart(3, '0')}`,   // Forge demand sources are kind:key
    };
  });
  const productionOrders = Array.from({ length: 42 }, (_, index) => ({
    partNumber: products[(index * 3 + Math.floor(index / 5)) % products.length].partNumber,
    quantity: 6 + (index % 7) * 2,
    dueDate: addDays(3 + (index * 5 % 28)),
    priority: index % 9 + 1,
    initialState: index < 10 ? 'Planned' : index >= 38 ? 'Held' : 'Released',
    sourceKey: `WW-DEMO-WO-${String(index + 1).padStart(3, '0')}`,
  }));
  return { demandLines, productionOrders };
}
