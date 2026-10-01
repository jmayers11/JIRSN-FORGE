// Builds a SAMPLE plant through the real Forge API (development build only), so every screen has
// records to show. Nothing is written to the database directly: each step is an ordinary command,
// audited, with the two-person rules applied (the engineer sets up, others approve and release).
//
// Usage: node tools/seed-sample-plant.mjs [http://localhost:5080] [admin-subject]
// Sign in afterwards as: planner (Michael Carter, planner), planner2, eng, approver, tech or dev-admin.

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
  if (existing.includes('eng')) throw new Error('This database already has the sample people. The sample plant is set up once per database.');

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

  console.log(`\nSample plant ready. Open ${base}/ and sign in as "planner" (Michael Carter).`);
  console.log('Other sample sign-ins: planner2, eng, approver, tech, dev-admin. Everything shown is sample data.');
}

main().catch(e => { console.error(`\nSample setup stopped: ${e.message}`); process.exitCode = 1; });
