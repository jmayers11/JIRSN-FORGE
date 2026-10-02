// Builds a SAMPLE plant through the real Forge API (development build only), so every screen has
// records to show. Nothing is written to the database directly: each step is an ordinary command,
// audited, with the two-person rules applied (the engineer sets up, others approve and release).
//
// Usage: node tools/seed-sample-plant.mjs [http://localhost:5080] [admin-subject]
// Sign in afterwards as: planner (Michael Carter, planner), planner2, eng, approver, tech or dev-admin;
// for materials: stores, ivy, bob, pat; for quality and shipping: insp, qe, qm, ship; pm and mp.
// Run it again on an older sample plant to add only what it is missing.

const base = (process.argv[2] ?? 'http://localhost:5080').replace(/\/+$/, '');
const adminSubject = process.argv[3] ?? 'dev-admin';
let n = 0;

async function call(subject, method, path, body) {
  const headers = { 'X-Forge-Dev-Subject': subject, Accept: 'application/json' };
  if (method === 'POST') {
    headers['Content-Type'] = 'application/json';
    headers['Idempotency-Key'] = `sample-${Date.now()}-${++n}`;
  }
  const r = await fetch(base + path, { method, headers, body: body ? JSON.stringify({ reason: 'Sample plant setup', ...body }) : undefined });
  const text = await r.text();
  if (!r.ok) throw new Error(`${method} ${path} as ${subject} -> ${r.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const post = (who, path, body = {}) => call(who, 'POST', path, body);
const get = (who, path) => call(who, 'GET', path);
const step = s => console.log(`- ${s}`);

const people = {};
async function person(subject, displayName, ...roles) {
  const r = await post(adminSubject, '/api/kernel/people', { idpSubject: subject, displayName });
  people[subject] = r.entityId;
  for (const role of roles) await post(adminSubject, `/api/kernel/people/${r.entityId}/roles`, { role });
  return r.entityId;
}

async function main() {
  try { await fetch(`${base}/health/ready`); } catch { throw new Error(`Forge is not running at ${base}. Start run-forge.cmd first.`); }
  const existing = (await get(adminSubject, '/api/kernel/people')).map(p => p.idpSubject);
  if (existing.includes('eng')) {
    // An earlier sample plant: add only what is new (Phase 6 materials), once.
    if (existing.includes('pm')) throw new Error('This database already has the full sample plant. It is set up once per database.');
    if (!existing.includes('stores')) {
      console.log('Sample plant found; adding sample materials (inventory, purchase orders, demand).');
      await seedMaterials();
    }
    if (!existing.includes('insp')) {
      console.log('Adding sample quality and shipping.');
      await seedQuality();
    }
    console.log('Adding sample programs and preventive maintenance.');
    await seedPrograms();
    return finish();
  }

  step('People and roles (each duty held by a different person)');
  await person('eng', 'Erin Engineer', 'ManufacturingEngineer', 'TrainingCoordinator', 'SafetyEngineer', 'Supervisor');
  await person('approver', 'Alex Approver', 'SafetyApprover', 'Certifier', 'EngineeringApprover');
  await person('planner', 'Michael Carter', 'Planner', 'Supervisor');
  await person('planner2', 'Paula Planner', 'Planner');
  await person('tech', 'Tom Technician', 'MaintenanceTechnician');

  step('Qualifications');
  for (const [code, name, kind] of [['MATL-I', 'Material handling', 'Process'], ['LASER-I', 'Laser cutting', 'Machine'], ['SLIT-II', 'Slitting Level II', 'Machine'],
    ['FIRST-AID', 'First aid', 'Safety'], ['PAINT-I', 'Paint', 'Process'], ['ASSY-I', 'Assembly', 'Process'], ['INSP-I', 'Inspection', 'Process'], ['SHIP-I', 'Shipping', 'Process']]) {
    await post('eng', '/api/competency/qualification-types', { code, name, kind, validityDays: 365 });
  }

  step('Site, safety areas, work centers and machines');
  await post('eng', '/api/resources/sites', { code: 'PLANT1', name: 'Sample Plant', timeZone: 'America/New_York' });
  const stages = [['RAW', 'RAW-WC', 'Raw Material'], ['FAB', 'FAB-WC', 'Fabrication'], ['SLITTING', 'SLIT-WC', 'Slitting'], ['PAINT', 'PAINT-WC', 'Paint'],
    ['ASSEMBLY', 'ASSY-WC', 'Assembly'], ['INSPECTION', 'INSP-WC', 'Inspection'], ['SHIPPING', 'SHIP-WC', 'Shipping']];
  for (const [area, wc, name] of stages) {
    await post('eng', '/api/resources/safety-areas', { code: area, name, parent: 'PLANT1' });
    await post('eng', '/api/resources/work-centers', { code: wc, name, parent: area });
  }
  for (const [code, name] of [['RECEIVE', 'Receive and stage material'], ['LASER_CUT', 'Laser cut'], ['RUN_SLITTER', 'Run slitter'], ['PAINT', 'Paint'],
    ['ASSEMBLE', 'Assemble'], ['INSPECT', 'Inspect'], ['SHIP', 'Pack and ship']]) {
    await post('eng', '/api/resources/activity-types', { code, name });
  }
  const machines = [['LASER-1', 'Laser 1', 'FAB-WC', 'LASER_CUT', 'LASER-I'], ['LASER-2', 'Laser 2', 'FAB-WC', 'LASER_CUT', 'LASER-I'],
    ['SLITTER-A', 'Slitter A', 'SLIT-WC', 'RUN_SLITTER', 'SLIT-II'], ['SLITTER-B', 'Slitter B', 'SLIT-WC', 'RUN_SLITTER', 'SLIT-II'],
    ['PAINT-BOOTH', 'Paint booth', 'PAINT-WC', 'PAINT', 'PAINT-I'], ['CMM-1', 'CMM 1', 'INSP-WC', 'INSPECT', 'INSP-I']];
  for (const [code, name, wc, activity, qual] of machines) {
    await post('eng', '/api/resources/machines', { code, name, parent: wc });
    await post('eng', `/api/resources/machines/${code}/activities`, { activityCode: activity, operatorQualifications: [qual] });
  }
  for (const [wc, activity] of [['RAW-WC', 'RECEIVE'], ['ASSY-WC', 'ASSEMBLE'], ['SHIP-WC', 'SHIP']]) {
    await post('eng', `/api/resources/work-centers/${wc}/activities`, { activityCode: activity });
  }

  step('Safety rule: two people at the slitters (authored by the engineer, published by the approver)');
  await post('eng', '/api/safety-rules/drafts', {
    code: 'SLIT-AREA', title: 'Two-person Slitting coverage', scopeKind: 'SafetyArea', scopeCode: 'SLITTING', activityCodes: ['RUN_SLITTER'],
    minimumPhysicalPeople: 2, prohibitLoneWork: true,
    slots: [{ name: 'Operator', count: 1, satisfiedBy: ['SLIT-II'], traineeMayFill: false }, { name: 'Safety responder', count: 1, satisfiedBy: ['FIRST-AID', 'SLIT-II'], traineeMayFill: false }],
  });
  await post('approver', '/api/safety-rules/SLIT-AREA/versions/1/publish', { reason: 'Reviewed against EHS procedure SP-12' });

  step('Around-the-clock shift (so the sample shows work whenever you open it)');
  await post('eng', '/api/resources/shifts', { code: 'ALL', siteCode: 'PLANT1', name: 'Around the clock', start: '00:00:00', durationMinutes: 1440, days: [1, 2, 3, 4, 5, 6, 7] });
  for (const [, wc] of stages) await post('eng', `/api/resources/work-centers/${wc}/shifts`, { shiftCode: 'ALL' });

  step('Workers: trained by the engineer, certified by the approver, on shift');
  const workers = [['sarah', 'Sarah Lopez', 'SLIT-WC', ['SLIT-II', 'FIRST-AID'], { machineCode: 'SLITTER-A', activityCode: 'RUN_SLITTER' }],
    ['marcus', 'Marcus Reed', 'SLIT-WC', ['SLIT-II'], { machineCode: 'SLITTER-B', activityCode: 'RUN_SLITTER' }],
    ['jennifer', 'Jennifer Wu', 'PAINT-WC', ['PAINT-I', 'FIRST-AID'], { machineCode: 'PAINT-BOOTH', activityCode: 'PAINT' }],
    ['luis', 'Luis Ortega', 'FAB-WC', ['LASER-I', 'MATL-I'], { machineCode: 'LASER-1', activityCode: 'LASER_CUT' }],
    ['dana', 'Dana Kim', 'ASSY-WC', ['ASSY-I', 'SHIP-I'], { workCenterCode: 'ASSY-WC', activityCode: 'ASSEMBLE' }],
    ['priya', 'Priya Shah', 'INSP-WC', ['INSP-I', 'SHIP-I', 'MATL-I'], { machineCode: 'CMM-1', activityCode: 'INSPECT' }],
    ['omar', 'Omar Haddad', 'RAW-WC', ['MATL-I'], { workCenterCode: 'RAW-WC', activityCode: 'RECEIVE' }],
    ['kelly', 'Kelly Brooks', 'SHIP-WC', ['SHIP-I', 'MATL-I'], { workCenterCode: 'SHIP-WC', activityCode: 'SHIP' }]];
  for (const [subject, name, home, quals, assignment] of workers) {
    const id = await person(subject, name);
    await post('eng', `/api/workforce/workers/${id}`, { homeWorkCenterCode: home });
    for (const code of quals) {
      await post('eng', `/api/competency/people/${id}/training`, { code });
      await post('approver', `/api/competency/people/${id}/certifications`, { code, evidence: 'Practical assessment on file', reason: 'Assessment passed' });
    }
    await post('eng', `/api/workforce/workers/${id}/presence`, { kind: 'SupervisorConfirmed' });
    await post('eng', `/api/workforce/workers/${id}/shift`, { shiftCode: 'ALL' });
    await post('eng', `/api/workforce/workers/${id}/assignment`, { ...assignment, reason: 'Shift start' });
  }

  step('Parts, BOM and routing through all seven work centers; released by the approver');
  await post('eng', '/api/engineering/items', { partNumber: 'COIL-AL', description: 'Aluminum coil', unitOfMeasure: 'KG', makeBuy: 'Buy', leadTimeDays: 14 });
  await post('eng', '/api/engineering/items', { partNumber: 'PANEL-500', description: 'Painted panel assembly', unitOfMeasure: 'EA', makeBuy: 'Make', leadTimeDays: 5 });
  await post('eng', '/api/engineering/items/COIL-AL/revisions', { revision: 'A' });
  await post('eng', '/api/engineering/items/PANEL-500/revisions', { revision: 'A' });
  await post('eng', '/api/engineering/items/PANEL-500/revisions/A/bom', { lineNumber: 1, componentPartNumber: 'COIL-AL', quantityPer: 3.2, scrapPercent: 4 });
  const routing = [[10, 'Receive coil', 'RAW-WC', 'RECEIVE', [], ['MATL-I'], 10, 0.2], [20, 'Laser cut blanks', 'FAB-WC', 'LASER_CUT', ['LASER-1', 'LASER-2'], [], 30, 1.5],
    [30, 'Slit to width', 'SLIT-WC', 'RUN_SLITTER', ['SLITTER-A', 'SLITTER-B'], [], 30, 1.2], [40, 'Paint', 'PAINT-WC', 'PAINT', ['PAINT-BOOTH'], [], 45, 1.0],
    [50, 'Assemble', 'ASSY-WC', 'ASSEMBLE', [], ['ASSY-I'], 15, 2.0], [60, 'Inspect', 'INSP-WC', 'INSPECT', ['CMM-1'], [], 10, 0.5],
    [70, 'Pack and ship', 'SHIP-WC', 'SHIP', [], ['SHIP-I'], 10, 0.3]];
  for (const [sequence, description, workCenterCode, activityCode, eligibleMachines, requiredQualifications, setupMinutes, runMinutesPerUnit] of routing) {
    await post('eng', '/api/engineering/items/PANEL-500/revisions/A/routing', { sequence, description, workCenterCode, activityCode, eligibleMachines, requiredQualifications, setupMinutes, runMinutesPerUnit, crewSize: 1 });
  }
  await post('approver', '/api/engineering/items/COIL-AL/revisions/A/release', { reason: 'ECN-1001' });
  await post('approver', '/api/engineering/items/PANEL-500/revisions/A/release', { reason: 'ECN-1001' });

  step('Orders: set up by the engineer, released by the planner');
  const day = d => new Date(Date.now() + d * 864e5).toISOString().slice(0, 10);
  for (const [qty, due, priority] of [[40, 3, 2], [60, 4, 3], [120, 2, 1], [30, 6, 4], [80, 5, 3], [50, 9, 5], [400, 2, 2], [300, 3, 3]]) {
    const o = await post('eng', '/api/orders/', { partNumber: 'PANEL-500', quantity: qty, dueDate: day(due), priority });
    await post('planner', `/api/orders/${o.entityId}/release`, { reason: 'Customer order' });
  }

  step('Maintenance: Slitter B down now, CMM calibration planned tomorrow');
  await post('tech', '/api/maintenance/machines/SLITTER-B/down', { description: 'Blade drive fault' });
  const tomorrow = new Date(Date.now() + 26 * 36e5);
  await post('tech', '/api/maintenance/machines/CMM-1/planned', { startsAt: tomorrow.toISOString(), endsAt: new Date(tomorrow.getTime() + 4 * 36e5).toISOString(), description: 'Annual calibration' });

  step('Approved PTO: Jennifer away tomorrow');
  await post('eng', `/api/workforce/workers/${people.jennifer}/unavailability`, { kind: 'ApprovedPto', startsAt: new Date(Date.now() + 20 * 36e5).toISOString(), endsAt: new Date(Date.now() + 44 * 36e5).toISOString(), reason: 'Approved PTO from HR' });

  step('Schedule: proposed by Michael Carter, approved by Paula Planner');
  const s = await post('planner', '/api/planning/schedules', { horizonDays: 14 });
  await post('planner2', `/api/planning/schedules/${s.entityId}/approve`, { reason: 'Reviewed outage and PTO impact' });

  step('Shop floor: a kiosk, badges, a trainer qualification, a trainee, and some reported work');
  await person('kiosk', 'Slitting Kiosk', 'ShopFloorTerminal');
  await post('eng', '/api/competency/qualification-types', { code: 'TRAIN-SLIT', name: 'Slitting trainer', kind: 'Trainer', validityDays: 365 });
  await post('eng', '/api/competency/qualification-types/TRAIN-SLIT/prerequisites', { prerequisiteCode: 'SLIT-II' });
  await post('eng', `/api/competency/people/${people.sarah}/training`, { code: 'TRAIN-SLIT' });
  await post('approver', `/api/competency/people/${people.sarah}/certifications`, { code: 'TRAIN-SLIT', evidence: 'Train-the-trainer course', reason: 'Trainer certified' });
  const alex = await person('alex', 'Alex Rivera');
  await post('eng', `/api/workforce/workers/${alex}`, { homeWorkCenterCode: 'SLIT-WC' });
  await post('eng', `/api/workforce/workers/${alex}/shift`, { shiftCode: 'ALL' });
  await post('eng', `/api/competency/people/${alex}/training`, { code: 'SLIT-II' });
  const badges = {};
  let next = 1001;
  for (const subject of ['sarah', 'marcus', 'jennifer', 'luis', 'dana', 'priya', 'omar', 'kelly', 'alex']) {
    badges[subject] = String(next++);
    await post(adminSubject, `/api/workforce/people/${people[subject]}/badge`, { badge: badges[subject] });
  }
  await post('kiosk', '/api/shopfloor/pin', { badge: badges.sarah, newPin: '2468' });
  for (const subject of ['omar', 'luis', 'alex']) await post('kiosk', '/api/shopfloor/clock-in', { badge: badges[subject] });

  // Omar receives coil for the first two orders; Luis starts cutting the first.
  const queue = (await get('planner', '/api/orders/')).filter(o => o.status === 'Released').map(o => o.orderNumber).sort((a, b) => a - b);
  for (const order of queue.slice(0, 2)) {
    await post('kiosk', '/api/shopfloor/start', { badge: badges.omar, orderNumber: order, sequence: 10 });
    const qty = (await get('planner', `/api/orders/${order}`)).quantity;
    await post('kiosk', '/api/shopfloor/report', { badge: badges.omar, orderNumber: order, sequence: 10, good: qty, scrap: 0, complete: true });
  }
  await post('kiosk', '/api/shopfloor/start', { badge: badges.luis, orderNumber: queue[0], sequence: 20, machineCode: 'LASER-1' });
  await post('kiosk', '/api/shopfloor/report', { badge: badges.luis, orderNumber: queue[0], sequence: 20, good: 12, scrap: 1, scrapReason: 'Setup', complete: false });

  await seedMaterials();
  await seedQuality();
  await seedPrograms();
  console.log(`Sample badges: ${Object.entries(badges).map(([k, v]) => `${k}=${v}`).join(', ')}. Sarah is a trainer (PIN 2468); Alex is in training.`);
  finish();
}

function finish() {
  console.log(`\nSample plant ready. Open ${base}/ and sign in as "planner" (Michael Carter).`);
  console.log('Other sample sign-ins: planner2, eng, approver, tech, dev-admin, "kiosk" for the Shop Floor terminal,');
  console.log('for materials: stores (material handler), ivy (inventory controller), bob (buyer), pat (purchasing manager);');
  console.log('for quality and shipping: insp (inspector), qe (quality engineer), qm (quality manager), ship (shipper);');
  console.log('for programs and maintenance: pm (program manager), mp (maintenance planner).');
  console.log('Everything shown is sample data.');
}

// Phase 6: locations, opening stock, a purchase order (approved by someone other than the buyer),
// incoming inspection, a count waiting for approval, demand and item planning parameters.
async function seedMaterials() {
  const day = d => new Date(Date.now() + d * 864e5).toISOString().slice(0, 10);
  step('Materials: people (each duty held by a different person)');
  await person('stores', 'Sam Stores', 'MaterialHandler');
  await person('ivy', 'Ivy Inventory', 'InventoryController');
  await person('bob', 'Bob Buyer', 'Buyer');
  await person('pat', 'Pat Purchasing', 'PurchasingManager');

  step('Materials: locations and opening stock');
  for (const [code, name, kind] of [['STORES', 'Main stores', 'Stores'], ['RECV', 'Receiving dock', 'Receiving'], ['QA-HOLD', 'Quarantine cage', 'Quarantine']]) {
    await post('ivy', '/api/inventory/locations', { code, siteCode: 'PLANT1', name, kind });
  }
  await post('stores', '/api/inventory/receipts', { partNumber: 'COIL-AL', quantity: 2000, locationCode: 'STORES', reason: 'Opening balance from the physical count' });
  await post('planner', '/api/engineering/items/COIL-AL/planning', { leadTimeDays: 14, safetyStock: 500, lotPolicy: 'FixedQuantity', orderQuantity: 1000, orderMultiple: 0 });

  step('Materials: a purchase order raised by Bob, approved by Pat, partly received into inspection');
  const po = await post('bob', '/api/procurement/purchase-orders', { supplier: 'Alcoa', lines: [{ partNumber: 'COIL-AL', quantity: 1000, promisedDate: day(5) }] });
  await post('pat', `/api/procurement/purchase-orders/${po.entityId}/approve`, { reason: 'Within budget' });
  await post('stores', '/api/inventory/receipts', { partNumber: 'COIL-AL', quantity: 200, locationCode: 'RECV', poNumber: Number(po.entityId), inspect: true });
  await post('bob', '/api/procurement/purchase-orders', { supplier: 'Novelis', lines: [{ partNumber: 'COIL-AL', quantity: 2000, promisedDate: day(20) }] });

  step('Materials: coil issued to the first order, a count waiting for a second person');
  const released = (await get('planner', '/api/orders/')).filter(o => o.status === 'Released').map(o => o.orderNumber).sort((a, b) => a - b);
  if (released.length) {
    const first = await get('planner', `/api/orders/${released[0]}`);
    const coil = first.materials.find(m => m.componentPartNumber === 'COIL-AL');
    if (coil) await post('stores', '/api/inventory/issues', { orderNumber: first.orderNumber, partNumber: 'COIL-AL', quantity: coil.requiredQuantity, locationCode: 'STORES' });
  }
  const onHand = (await get('stores', '/api/inventory/stock?part=COIL-AL')).filter(b => b.locationCode === 'STORES' && b.status === 'Available').reduce((t, b) => t + b.quantity, 0);
  await post('stores', '/api/inventory/counts', { partNumber: 'COIL-AL', locationCode: 'STORES', counted: Math.max(0, Math.round(onHand) - 10) });

  step('Materials: customer demand and a forecast for MRP');
  await post('planner', '/api/planning/demand', { partNumber: 'PANEL-500', quantity: 150, needDate: day(21), priority: 2, kind: 'CustomerOrder', customer: 'Acme Fabrication', reference: 'PO-77812' });
  await post('planner', '/api/planning/demand', { partNumber: 'PANEL-500', quantity: 200, needDate: day(35), priority: 5, kind: 'Forecast' });

  step('Schedule: re-run with material availability, approved by a second planner');
  const s = await post('planner', '/api/planning/schedules', { horizonDays: 14 });
  await post('planner2', `/api/planning/schedules/${s.entityId}/approve`, { reason: 'Reviewed material shortages' });
}

// Phase 7a: gauges, inspection points, a failed inspection that raised an NCR (holding its order),
// a corrective action, and a shipment packed by stores waiting for the shipper.
async function seedQuality() {
  const day = d => new Date(Date.now() + d * 864e5).toISOString().slice(0, 10);
  step('Quality: people (inspector, quality engineer, quality manager, shipper)');
  await person('insp', 'Ines Inspector', 'Inspector');
  await person('qe', 'Quinn Quality', 'QualityEngineer');
  await person('qm', 'Morgan Quality Manager', 'QualityManager');
  await person('ship', 'Shay Shipper', 'Shipper');

  step('Quality: gauges and inspection points on the PANEL-500 routing');
  await post('qe', '/api/quality/gauges', { code: 'CAL-01', description: 'Digital caliper 0-600 mm', calibrationDue: day(120), certificate: 'CERT-2026-0412' });
  await post('qe', '/api/quality/gauges', { code: 'TAPE-07', description: 'Steel tape 3 m', calibrationDue: day(-3), certificate: 'CERT-2025-1180' });
  await post('qe', '/api/quality/plans', { partNumber: 'PANEL-500', sequence: 20, title: 'First-piece blank check', characteristics: [
    { name: 'Blank length', kind: 'Variable', nominal: 500, lower: 499.5, upper: 500.5, unit: 'mm' },
    { name: 'Edges free of dross', kind: 'Attribute' }] });
  await post('qe', '/api/quality/plans', { partNumber: 'PANEL-500', sequence: 40, title: 'Paint coverage', characteristics: [{ name: 'Full coverage, no runs', kind: 'Attribute' }] });
  await post('qe', '/api/quality/plans', { partNumber: 'PANEL-500', sequence: 60, title: 'Final inspection', characteristics: [
    { name: 'Overall width', kind: 'Variable', nominal: 480, lower: 479, upper: 481, unit: 'mm' },
    { name: 'Label and hardware', kind: 'Attribute' }] });

  step('Quality: a blank out of tolerance raises an NCR on the first order');
  const started = (await get('planner', '/api/orders/')).filter(o => o.status === 'Released').map(o => o.orderNumber).sort((a, b) => a - b);
  if (started.length) {
    const r = await post('insp', '/api/quality/inspections', { orderNumber: started[0], sequence: 20, quantity: 3, gaugeCode: 'CAL-01', rejectedQuantity: 1, notes: 'Third blank long',
      findings: [{ number: 1, measured: 500.9 }, { number: 2, accepted: true }] });
    const ncr = (await get('qe', '/api/quality/ncrs')).find(x => x.orderNumber === started[0]);
    if (ncr) await post('qe', '/api/quality/capas', { ncrId: ncr.ncrId, title: 'Laser blank length drift', rootCause: 'Under investigation: suspect table squareness', action: 'Check and re-square LASER-1 table; add first-piece check', dueDate: day(14) });
    console.log(`  ${r.message}`);
  }

  step('Shipping: finished panels in stock and a shipment packed for Acme, waiting for the shipper');
  await post('ivy', '/api/inventory/locations', { code: 'FG', siteCode: 'PLANT1', name: 'Finished goods', kind: 'Stores' });
  await post('ivy', '/api/inventory/locations', { code: 'DOCK', siteCode: 'PLANT1', name: 'Shipping dock', kind: 'Shipping' });
  await post('stores', '/api/inventory/receipts', { partNumber: 'PANEL-500', quantity: 40, locationCode: 'FG', reason: 'Opening balance of finished panels' });
  const acme = (await get('planner', '/api/planning/demand')).find(d => d.customer === 'Acme Fabrication');
  const shipment = await post('stores', '/api/inventory/shipments', { demandId: acme?.demandId ?? null, customer: acme ? null : 'Acme Fabrication', reference: 'PO-77812 partial', shipTo: 'Acme Fabrication, Dock 2',
    lines: [{ partNumber: 'PANEL-500', quantity: 25, locationCode: 'FG' }] });
  await post('stores', `/api/inventory/shipments/${shipment.entityId}/pack`);
}

// Phase 7b: Program Falcon with a contract, CLINs, deliveries (two planned as demand), requirements and
// milestones; preventive-maintenance plans (one lock-out plan overdue) and a breakdown being repaired.
async function seedPrograms() {
  const day = d => new Date(Date.now() + d * 864e5).toISOString().slice(0, 10);
  step('Programs: Program Falcon, its contract and deliveries');
  await person('pm', 'Paige Program', 'ProgramManager');
  await person('mp', 'Max Maintenance Planner', 'MaintenancePlanner');
  const falcon = (await post('pm', '/api/programs/', { code: 'FALCON', name: 'Program Falcon', customer: 'US Air Force', description: 'Painted panel assemblies for the Falcon airframe upgrade' })).entityId;
  const everyone = await get(adminSubject, '/api/kernel/people');
  for (const who of ['pm', 'planner', 'planner2', 'stores', 'ship']) {
    const p = everyone.find(x => x.idpSubject === who);
    if (p) await post(adminSubject, `/api/kernel/people/${p.personId}/programs`, { programId: falcon, reason: 'Falcon program team' });
  }
  const contract = (await post('pm', `/api/programs/${falcon}/contracts`, { number: 'FA8650-26-C-0042', customer: 'US Air Force', title: 'Falcon panel assemblies, Lot 1', periodStart: day(-30), periodEnd: day(335), ceiling: 180000 })).entityId;
  await post('pm', `/api/programs/contracts/${contract}/clins`, { clin: '0001', description: 'Painted panel assembly', partNumber: 'PANEL-500', quantity: 120, unitPrice: 850 });
  await post('pm', `/api/programs/contracts/${contract}/clins`, { clin: '0001AA', parentClin: '0001', description: 'First article inspection report' });
  await post('pm', `/api/programs/contracts/${contract}/clins`, { clin: '0002', description: 'Technical data package' });
  const deliveries = [];
  for (const [quantity, due] of [[40, 28], [40, 56], [40, 84]]) {
    deliveries.push((await post('pm', `/api/programs/contracts/${contract}/deliveries`, { clin: '0001', quantity, dueDate: day(due) })).entityId);
  }
  await post('pm', `/api/programs/contracts/${contract}/requirements`, { code: 'AS9102', text: 'First article inspection report with the first delivery.' });
  await post('pm', `/api/programs/contracts/${contract}/requirements`, { code: 'DFARS 252.225-7009', text: 'Specialty metals must be melted or produced in a qualifying country.' });
  const cdr = (await post('pm', `/api/programs/${falcon}/milestones`, { title: 'Critical design review', dueDate: day(-10) })).entityId;
  await post('pm', `/api/programs/milestones/${cdr}/complete`, { completedOn: day(-12) });
  await post('pm', `/api/programs/${falcon}/milestones`, { title: 'First article inspection', dueDate: day(21) });
  for (const id of deliveries.slice(0, 2)) await post('planner', `/api/planning/demand/from-delivery/${id}`, { priority: 2 });
  await post('planner', '/api/orders/', { partNumber: 'PANEL-500', quantity: 40, dueDate: day(24), priority: 2, programId: falcon });

  step('Maintenance: PM plans (one lock-out plan overdue) and a breakdown under repair');
  await post('mp', '/api/maintenance/plans', { machineCode: 'PAINT-BOOTH', title: 'Booth filter change', intervalDays: 14, firstDue: day(3), durationMinutes: 45,
    tasks: ['Lock out booth fans', 'Replace intake and exhaust filters', 'Record manometer reading'], lockOutWhenOverdue: false, graceDays: 0 });
  await post('mp', '/api/maintenance/plans', { machineCode: 'SLITTER-A', title: 'Knife and guard inspection', intervalDays: 30, firstDue: day(-4), durationMinutes: 90,
    tasks: ['Lock out and tag out', 'Inspect and rotate knives', 'Check guard interlocks'], lockOutWhenOverdue: true, graceDays: 2 });
  await post('mp', '/api/maintenance/plans', { machineCode: 'LASER-1', title: 'Lens and nozzle clean', intervalDays: 7, firstDue: day(1), durationMinutes: 30,
    tasks: ['Clean focus lens', 'Replace nozzle if worn', 'Test cut'], lockOutWhenOverdue: false, graceDays: 0 });
  await post('tech', '/api/maintenance/machines/CMM-1/down', { description: 'Probe crash on fixture; stylus bent' });
}

main().catch(e => { console.error(`\nSample setup stopped: ${e.message}`); process.exitCode = 1; });
