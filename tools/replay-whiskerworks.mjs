// Replays the WhiskerWorks demo on its own isolated database (Sprint 9/10). Run against a demo host started by
// run-whiskerworks-demo.cmd (Forge:Demo:Enabled, database forge_whiskerworks_demo, port 5090). It never touches the PLANT1 preview:
// every write goes to --base, and the run stops unless that host reports demo mode and a demo-only database.
//
//   node tools/replay-whiskerworks.mjs --base=http://localhost:5090 [--admin=dev-admin] [--out=.forge-logs/whiskerworks-replay.json]
//
// First run: seeds the four WhiskerWorks datasets through the audited APIs, designates WW-DEMO / WW-P01 and saves the baseline.
// Every run: resets to the baseline, then replays the clock-driven checks and records evidence with pass/fail per assertion.
import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { delayedPromiseDate, selectDayShiftScenarioRoster } from '../src/Forge.Host/wwwroot/js/data/whiskerworks-demo.js';
import { allItems, subassemblyBoms } from './whiskerworks-commercial.mjs';
import { scenarioQualificationExpiry } from './whiskerworks-plant.mjs';

const args = process.argv.slice(2);
const value = name => args.find(argument => argument.startsWith(`--${name}=`))?.slice(name.length + 3);
const base = value('base');
const admin = value('admin') ?? 'dev-admin';
const out = value('out') ?? '.forge-logs/whiskerworks-replay.json';
if (!base) throw new Error('Pass --base=http://localhost:<demo port>; there is no default target.');
const target = new URL(base);
if (!['localhost', '127.0.0.1', '::1'].includes(target.hostname)) throw new Error('Refusing a non-local replay target.');

const engineer = 'ww-demo-engineer';
const planner = 'ww-demo-planner';
const evidence = { startedAt: new Date().toISOString(), base, steps: [], assertions: [] };
const step = (name, detail = {}) => { evidence.steps.push({ name, at: new Date().toISOString(), ...detail }); console.log(`- ${name}`); };
const check = (name, passed, detail = {}) => { evidence.assertions.push({ name, passed: !!passed, ...detail }); console.log(`  ${passed ? 'PASS' : 'FAIL'} ${name}`); };

async function request(method, path, who, body, idempotencyKey) {
  const headers = { 'X-Forge-Dev-Subject': who, Accept: 'application/json' };
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    headers['Idempotency-Key'] = idempotencyKey ?? `ww-replay-${randomUUID()}`;
  }
  const response = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30 * 60_000) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} as ${who} returned ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const get = (path, who = admin) => request('GET', path, who);
const post = (path, body, who = admin, idempotencyKey) => request('POST', path, who, body, idempotencyKey);

async function ready() {
  for (let attempt = 0; attempt < 120; attempt++) {
    try { const r = await fetch(`${base}/health/ready`); if (r.ok) return; } catch { /* starting */ }
    await new Promise(resolve => setTimeout(resolve, 2000));
  }
  throw new Error(`Forge is not ready at ${base}.`);
}

function seed(script, extra) {
  const run = spawnSync(process.execPath, [`tools/${script}`, '--apply', `--base=${base}`, ...extra], { stdio: 'inherit' });
  if (run.status !== 0) throw new Error(`${script} failed with exit code ${run.status}.`);
  step(`Applied ${script}`);
}

async function ensureReader() {
  const people = await get('/api/kernel/people');
  if (people.some(person => person.idpSubject === engineer)) return;
  const created = await post('/api/kernel/people', { idpSubject: engineer, displayName: 'WhiskerWorks Demo Engineer', reason: 'WhiskerWorks replay reader' });
  for (const role of ['ManufacturingEngineer', 'TrainingCoordinator', 'SafetyEngineer', 'QualityEngineer', 'MaintenancePlanner', 'Buyer', 'MaterialHandler', 'DocumentController'])
    await post(`/api/kernel/people/${created.entityId}/roles`, { role, reason: 'WhiskerWorks replay reader' });
}

async function cncMillReadiness() {
  const result = await get(`/api/readiness/machines/${encodeURIComponent(scenarioQualificationExpiry.machineCode)}?activity=CNC_MILL`, engineer);
  const output = result.output ?? result;
  const workforce = (output.dimensions ?? []).find(d => d.name === 'Workforce');
  return { outcome: workforce?.outcome ?? null, detail: workforce?.detail ?? null };
}

async function replayS07() {
  const parts = [
    { partNumber: 'WW-SENS-PROX-05', supplierCode: 'WW-SUP-009', parentPartNumber: 'WW-SENSOR-ARRAY-ASSY', label: 'sensor' },
    { partNumber: 'WW-BRG-608-2RS', supplierCode: 'WW-SUP-010', parentPartNumber: 'WW-ARENA-DRIVE', label: 'bearing' },
  ];
  const orders = await get('/api/procurement/purchase-orders', engineer);
  const targets = [];
  for (const target of parts) {
    const candidates = orders.flatMap(order => (order.lines ?? []).map(line => ({ order, line })))
      .filter(({ order, line }) => order.supplierCode === target.supplierCode && order.status === 'Approved'
        && line.partNumber === target.partNumber && line.status === 'Open' && Number(line.open) > 0);
    if (candidates.length !== 1) throw new Error(`S07 expected one approved open ${target.partNumber} line from ${target.supplierCode}; found ${candidates.length}.`);
    const { order, line } = candidates[0];
    const promisedFrom = line.promisedDate;
    const promisedTo = delayedPromiseDate(promisedFrom, 4);
    const partPlan = await get(`/api/planning/mrp?part=${encodeURIComponent(target.partNumber)}`, engineer);
    const itemPlan = (partPlan.output ?? partPlan).items?.find(item => item.partNumber === target.partNumber);
    const receipt = itemPlan?.receipts?.find(candidate => candidate.key === `PO${order.poNumber}/${line.lineNumber}`);
    const bom = subassemblyBoms.find(candidate => candidate.partNumber === target.parentPartNumber);
    const componentIndex = bom?.lines?.findIndex(([partNumber]) => partNumber === target.partNumber) ?? -1;
    const component = componentIndex >= 0 ? bom.lines[componentIndex] : null;
    const parent = allItems.find(item => item.partNumber === target.parentPartNumber);
    const available = Number(itemPlan?.available);
    const scheduledQuantity = Number(receipt?.quantity);
    const quantityPer = Number(component?.[1]);
    if (!Number.isFinite(available) || !Number.isFinite(scheduledQuantity) || !Number.isFinite(quantityPer)
      || quantityPer <= 0 || !Number.isFinite(Number(parent?.leadTimeDays))) {
      throw new Error(`S07 cannot derive a source-backed planned order for ${target.partNumber} from its MRP snapshot and released WhiskerWorks BOM.`);
    }
    // The existing finished-goods workload is intentionally well stocked. Add a temporary, ordinary
    // planned subassembly order so this replay has a real earlier component requirement to test.
    const productionQuantity = Math.floor(available / quantityPer) + 1;
    const componentNeed = productionQuantity * quantityPer;
    if (componentNeed > available + scheduledQuantity) {
      throw new Error(`S07 cannot isolate a delayed-receipt shortage for ${target.partNumber}: ${componentNeed} required, ${available} released and ${scheduledQuantity} already due.`);
    }
    const dueDate = new Date(`${promisedFrom}T00:00:00.000Z`);
    dueDate.setUTCDate(dueDate.getUTCDate() + Number(parent.leadTimeDays));
    const sourceOrder = await post('/api/orders/', {
      partNumber: target.parentPartNumber,
      quantity: productionQuantity,
      dueDate: dueDate.toISOString().slice(0, 10),
      priority: 1,
      reason: 'S07 isolated replay: schedule subassembly demand to verify delayed purchased-material impact',
    }, engineer, `ww-replay-s07-demand-${target.partNumber.toLowerCase()}-${promisedFrom}`);
    target.sourceOrderNumber = Number(sourceOrder.entityId ?? sourceOrder.orderNumber);
    target.componentNeed = componentNeed;
    target.componentLineNumber = (componentIndex + 1) * 10;
    target.stockAvailableBeforeNeed = available;
    target.scheduledQuantityBeforeDelay = scheduledQuantity;
    target.requirementDate = promisedFrom;
    target.originalReceiptDate = receipt?.dueDate ?? null;
    const baselineMrp = await get(`/api/planning/mrp?part=${encodeURIComponent(target.partNumber)}`, engineer);
    const baselineOutput = baselineMrp.output ?? baselineMrp;
    const baselineItem = baselineOutput.items?.find(candidate => candidate.partNumber === target.partNumber);
    const baselineRequirements = baselineItem?.requirements ?? [];
    const baselineRequirement = baselineRequirements.find(candidate => candidate.key === `WO${target.sourceOrderNumber}/${target.componentLineNumber}`);
    const baselineReceipt = baselineItem?.receipts?.find(candidate => candidate.key === `PO${order.poNumber}/${line.lineNumber}`);
    const baselineReschedule = (baselineOutput.exceptions ?? []).find(exception => exception.partNumber === target.partNumber
      && exception.kind === 'RescheduleIn' && String(exception.detail ?? '').includes(`PO ${order.poNumber} line ${line.lineNumber}`));
    const baselineSupplyCoversNeed = !!baselineRequirement && baselineRequirement.needDate === target.requirementDate
      && baselineReceipt?.dueDate === target.requirementDate
      && Number(baselineItem.available) + Number(baselineReceipt.quantity) >= Number(baselineRequirement.quantity);
    check(`S07 ${target.label} baseline order need is covered by the original receipt date`, baselineSupplyCoversNeed && !baselineReschedule, {
      orderNumber: target.sourceOrderNumber, requirement: baselineRequirement ?? null, receipt: baselineReceipt ?? null,
      baselineReschedule: baselineReschedule ?? null, candidateRequirements: baselineRequirements,
    });
    await post(`/api/procurement/purchase-orders/${order.poNumber}/lines/${line.lineNumber}/date`, {
      promisedDate: promisedTo,
      reason: `S07 replay: delay approved ${target.partNumber} supply by four calendar days`,
    }, engineer, `ww-replay-s07-${target.partNumber.toLowerCase()}-po-${order.poNumber}-line-${line.lineNumber}`);
    targets.push({ ...target, poNumber: Number(order.poNumber), lineNumber: Number(line.lineNumber), promisedFrom, promisedTo });
  }

  const ordersAfter = await get('/api/procurement/purchase-orders', engineer);
  const results = [];
  for (const target of targets) {
    const order = ordersAfter.find(candidate => Number(candidate.poNumber) === target.poNumber);
    const line = order?.lines?.find(candidate => Number(candidate.lineNumber) === target.lineNumber);
    const sourceMatches = !!line && order.status === 'Approved' && line.status === 'Open' && Number(line.open) > 0
      && line.promisedDate === target.promisedTo;
    const plan = await get(`/api/planning/mrp?part=${encodeURIComponent(target.partNumber)}`, engineer);
    const output = plan.output ?? plan;
    const item = output.items?.find(candidate => candidate.partNumber === target.partNumber);
    const receipt = item?.receipts?.find(candidate => candidate.key === `PO${target.poNumber}/${target.lineNumber}`);
    const requirements = item?.requirements ?? [];
    const requirement = requirements.find(candidate => candidate.key === `WO${target.sourceOrderNumber}/${target.componentLineNumber}`);
    const reschedule = (output.exceptions ?? []).find(exception => exception.partNumber === target.partNumber
      && exception.kind === 'RescheduleIn' && String(exception.detail ?? '').includes(`PO ${target.poNumber} line ${target.lineNumber}`));
    const earlierRequirement = !!requirement && requirement.needDate === target.requirementDate
      && requirement.needDate < (receipt?.dueDate ?? '') && Number(requirement.quantity) === target.componentNeed;
    const receiptMatches = receipt?.kind === 'PurchaseOrder' && receipt.dueDate === target.promisedTo && earlierRequirement;
    const rescheduleMatches = !!reschedule && String(reschedule.detail ?? '').includes(`due ${target.promisedTo}`)
      && String(reschedule.detail ?? '').includes(`below zero on ${target.requirementDate}`);
    const result = { ...target, sourceMatches, receipt: receipt ?? null, requirement: requirement ?? null,
      earlierRequirement, reschedule: reschedule ?? null, rescheduleMatches };
    results.push(result);
    check(`S07 ${target.label} PO date is persisted on the exact approved open line`, sourceMatches, {
      poNumber: target.poNumber, lineNumber: target.lineNumber, promisedFrom: target.promisedFrom,
      promisedTo: target.promisedTo, current: line ?? null,
    });
    check(`S07 ${target.label} exact planned-order requirement and delayed receipt appear in MRP`, receiptMatches, {
      receipt: receipt ?? null, requirement: requirement ?? null, earlierRequirement, candidateRequirements: requirements,
    });
    check(`S07 ${target.label} MRP RescheduleIn cites the exact delayed PO line and need date`, rescheduleMatches, {
      exception: reschedule ?? null, rescheduleMatches,
    });
  }
  evidence.s07 = {
    targets: results,
    scenarioDemand: 'Two temporary, reasoned production orders create source-backed component requirements through the normal Orders API.',
    scenarioShortage: 'Each original receipt covers its exact planned-order requirement; the four-day delay moves receipt after the need date and produces the exact MRP RescheduleIn.',
    supplierCorrespondence: 'not fabricated',
    disposition: 'The end-of-replay baseline reset archives and removes both temporary orders and both date changes from the active demo database.',
  };
  step('Replayed S07 sensor and bearing promise changes with source and MRP readback');
  return results;
}

async function machineDimension(machineCode, activity, name) {
  const result = await get(`/api/readiness/machines/${encodeURIComponent(machineCode)}?activity=${encodeURIComponent(activity)}`, engineer);
  const dimension = ((result.output ?? result).dimensions ?? []).find(d => d.name === name);
  return { outcome: dimension?.outcome ?? null, detail: dimension?.detail ?? null };
}

// S03: one of exactly two present, independently qualified ASM-03 operators calls out, so the SAFE-ASM-2P two-person
// rule is no longer met. Presence for the crew is confirmed first through the audited supervisor command (seeded
// presence lapses with the clock); nothing else about the crew is changed.
async function replayS03(simulatedNow) {
  const supervisor = 'ww-demo-supervisor';
  const now = Date.parse(simulatedNow);
  const workers = await get('/api/workforce/workers', supervisor);
  const ids = workers.map(w => w.personId);
  const records = await get(`/api/competency/people/qualifications?${ids.map(id => `personIds=${encodeURIComponent(id)}`).join('&')}`, engineer);
  const qualified = new Set(records.filter(r => r.code === 'QUAL-ASM-HVY' && r.status === 'Qualified' && (!r.expiresAt || Date.parse(r.expiresAt) > now)).map(r => r.personId));
  const crew = workers.filter(w => w.isActive && w.shiftCode === 'SHIFT-DAY' && w.assignment?.machineCode === 'ASM-03' && qualified.has(w.personId))
    .sort((a, b) => a.displayName.localeCompare(b.displayName));
  check('S03 finds exactly two independently qualified Day Shift operators assigned to ASM-03', crew.length === 2, { crew: crew.map(w => w.displayName) });
  if (crew.length !== 2) return;
  for (const worker of crew) {
    if (!worker.presentUntil || Date.parse(worker.presentUntil) <= now)
      await post(`/api/workforce/workers/${worker.personId}/presence`, { kind: 'SupervisorConfirmed', reason: 'S03 replay: crew confirmed on the floor' }, supervisor,
        `ww-replay-s03-presence-${worker.personId}`);
  }
  const safetyBefore = await machineDimension('ASM-03', 'ASSEMBLE_HEAVY', 'Safety');
  const [callout] = crew;
  const endsAt = new Date(now + 8 * 3600e3);
  await post(`/api/workforce/workers/${callout.personId}/unavailability`, {
    kind: 'CallOut', startsAt: new Date(now).toISOString(), endsAt: endsAt.toISOString(), reason: 'S03 replay: heavy assembly operator called out',
  }, supervisor, `ww-replay-s03-callout-${callout.personId}`);
  await post(`/api/workforce/workers/${callout.personId}/departure`, { reason: 'S03 replay: callout departure' }, supervisor, `ww-replay-s03-departure-${callout.personId}`);
  const safetyAfter = await machineDimension('ASM-03', 'ASSEMBLE_HEAVY', 'Safety');
  const after = new Map((await get('/api/workforce/workers', supervisor)).map(w => [w.personId, w]));
  const remaining = after.get(crew[1].personId);
  evidence.s03 = { callout: callout.displayName, partner: crew[1].displayName, safetyBefore, safetyAfter };
  check('S03 ASM-03 two-person safety is met while both qualified operators are present', safetyBefore.outcome === 'Pass', { safetyBefore });
  check('S03 ASM-03 safety fails after one of the two calls out, while the partner is still present', safetyAfter.outcome === 'Fail'
    && !!remaining?.presentUntil && Date.parse(remaining.presentUntil) > now, { safetyAfter, partnerPresentUntil: remaining?.presentUntil ?? null });
}

// Site-local wall-clock time to UTC, iterating on the zone offset (handles DST like the Scenario Center does).
function localToUtc(date, clock, timeZone) {
  const [year, month, day] = date.split('-').map(Number);
  const [hour, minute, second = 0] = String(clock).split(':').map(Number);
  const target = Date.UTC(year, month - 1, day, hour, minute, second);
  let guess = target;
  for (let attempt = 0; attempt < 4; attempt++) {
    const parts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).formatToParts(new Date(guess));
    const value = type => Number(parts.find(item => item.type === type)?.value);
    const correction = target - Date.UTC(value('year'), value('month') - 1, value('day'), value('hour'), value('minute'), value('second'));
    guess += correction;
    if (correction === 0) break;
  }
  return new Date(guess);
}
const siteDate = (at, timeZone) => new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(at));
const dateOffset = (date, days) => new Date(Date.parse(`${date}T12:00:00Z`) + days * 864e5).toISOString().slice(0, 10);
const isoWeekday = date => new Date(`${date}T12:00:00Z`).getUTCDay() || 7;

async function rejection(path, body, who, idempotencyKey) {
  try { await post(path, body, who, idempotencyKey); return null; } catch (error) { return error.message; }
}

// S14: a reviewed CNC Mill handoff for the last completed Day Shift gets two owned, dated recovery actions; the second
// depends on the first, and the server refuses to start it until the first is completed with evidence.
async function replayS14(simulatedNow, resources) {
  const supervisor = 'ww-demo-supervisor';
  const timeZone = resources.sites[0].timeZone;
  const shift = resources.shifts.find(item => item.code === 'SHIFT-DAY');
  const workers = await get('/api/workforce/workers', supervisor);
  const assignee = workers.filter(w => w.isActive && w.homeWorkCenterCode === 'WC-CNC-MILL' && w.shiftCode === 'SHIFT-DAY')
    .sort((a, b) => a.displayName.localeCompare(b.displayName))[0];
  const today = siteDate(simulatedNow, timeZone);
  let completedDate = null;
  for (let offset = 1; offset <= 8 && !completedDate; offset++) if ((shift?.days ?? []).includes(isoWeekday(dateOffset(today, -offset)))) completedDate = dateOffset(today, -offset);
  check('S14 has a CNC Mill Day Shift assignee and a completed Day Shift', !!shift && !!assignee && !!completedDate, { assignee: assignee?.displayName ?? null, completedDate });
  if (!shift || !assignee || !completedDate) return;
  const shiftFrom = localToUtc(completedDate, shift.start, timeZone);
  const shiftUntil = new Date(shiftFrom.getTime() + shift.durationMinutes * 60000);
  const firstDueAt = localToUtc(dateOffset(today, 1), '12:00:00', timeZone).toISOString();
  const secondDueAt = localToUtc(dateOffset(today, 1), '16:00:00', timeZone).toISOString();
  const created = await post('/api/execution/shifts/handoffs', { workCenterCode: 'WC-CNC-MILL', shiftCode: 'SHIFT-DAY', shiftFrom: shiftFrom.toISOString(),
    shiftUntil: shiftUntil.toISOString(), context: 'WW-S14 replay: CNC-01 coolant follow-up and readiness check.', reason: 'S14 replay handoff' }, supervisor);
  const handoffId = Number(created.entityId);
  await post(`/api/execution/shifts/handoffs/${handoffId}/review`, { reason: 'S14 replay review' }, supervisor);
  const actionsPath = `/api/execution/shifts/handoffs/${handoffId}/actions`;
  const first = Number((await post(actionsPath, { description: 'WW-S14: Inspect CNC-01 coolant follow-up', sourceKind: 'Machine', sourceReference: 'CNC-01',
    assignee: assignee.personId, dueAt: firstDueAt, blockedByActionIds: [], reason: 'S14 replay action' }, supervisor)).entityId);
  const second = Number((await post(actionsPath, { description: 'WW-S14: Verify CNC-01 readiness after inspection', sourceKind: 'Machine', sourceReference: 'CNC-01',
    assignee: assignee.personId, dueAt: secondDueAt, blockedByActionIds: [first], reason: 'S14 replay dependent action' }, supervisor)).entityId);
  const saved = await get(actionsPath, supervisor);
  const a = saved.find(x => x.actionId === first), b = saved.find(x => x.actionId === second);
  check('S14 saves both actions with their due times and the dependency', !!a && !!b && Date.parse(a.dueAt) === Date.parse(firstDueAt)
    && Date.parse(b.dueAt) === Date.parse(secondDueAt) && (b.blockedByActionIds ?? []).map(Number).includes(first), { first: a, second: b });
  const status = id => `/api/execution/shifts/actions/${id}/status`;
  await post(status(second), { expected: 'Assigned', status: 'Acknowledged', reason: 'S14 replay' }, supervisor);
  const blocked = await rejection(status(second), { expected: 'Acknowledged', status: 'InProgress', reason: 'S14 replay: try before the blocker' }, supervisor);
  for (const [expected, next] of [['Assigned', 'Acknowledged'], ['Acknowledged', 'InProgress'], ['InProgress', 'Completed']])
    await post(status(first), { expected, status: next, evidence: next === 'Completed' ? 'Coolant line inspected; no leak (fictional replay evidence)' : undefined, reason: 'S14 replay' }, supervisor);
  const unblocked = await rejection(status(second), { expected: 'Acknowledged', status: 'InProgress', reason: 'S14 replay: blocker done' }, supervisor);
  evidence.s14 = { handoffId, actions: [first, second], blocked, unblocked };
  check('S14 refuses to start the dependent action while its blocker is open', /action_dependencies_open/.test(blocked ?? ''), { blocked });
  check('S14 allows the dependent action once the blocker is completed with evidence', unblocked === null, { unblocked });
}

// S15 part 1: a CNC Mill Day Shift worker calls out and leaves while CNC-01 is down (S04).
async function replayS15Absence(simulatedNow) {
  const supervisor = 'ww-demo-supervisor';
  const now = Date.parse(simulatedNow);
  const workers = await get('/api/workforce/workers', supervisor);
  const from = new Date(now - 3600e3).toISOString(), to = new Date(now + 9 * 3600e3).toISOString();
  const unavailability = await get(`/api/workforce/unavailability?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`, supervisor);
  const mill = workers.filter(w => w.isActive && w.shiftCode === 'SHIFT-DAY' && (w.assignment?.workCenterCode ?? w.homeWorkCenterCode) === 'WC-CNC-MILL')
    .sort((a, b) => a.displayName.localeCompare(b.displayName));
  const worker = mill.find(w => !unavailability.some(u => u.personId === w.personId));
  if (!worker) return null;
  await post(`/api/workforce/workers/${worker.personId}/unavailability`, { kind: 'CallOut', startsAt: new Date(now).toISOString(),
    endsAt: new Date(now + 8 * 3600e3).toISOString(), reason: 'S15 replay: CNC Mill callout' }, supervisor);
  if (worker.presentUntil && Date.parse(worker.presentUntil) > now)
    await post(`/api/workforce/workers/${worker.personId}/departure`, { reason: 'S15 replay: callout departure' }, supervisor);
  return { personId: worker.personId, name: worker.displayName, startsAt: now, endsAt: now + 8 * 3600e3 };
}

// S15 part 2 (after the clock has moved past that shift): the outgoing handoff carries the open CNC-01 downtime and the
// recorded callout; its creator cannot acknowledge it, a different incoming operator can.
async function replayS15Handoff(simulatedNow, resources, absence) {
  const supervisor = 'ww-demo-supervisor';
  check('S15 records a CNC Mill callout while CNC-01 is down', !!absence, { absence });
  if (!absence) return;
  const timeZone = resources.sites[0].timeZone;
  const now = Date.parse(simulatedNow);
  const shifts = resources.shifts.filter(shift => (resources.workCenterShifts ?? []).some(x => x.workCenterCode === 'WC-CNC-MILL' && x.shiftCode === shift.code));
  const candidates = [];
  for (let offset = -1; offset <= 1; offset++) {
    const date = dateOffset(siteDate(absence.startsAt, timeZone), offset);
    for (const shift of shifts) {
      if (!(shift.days ?? []).includes(isoWeekday(date))) continue;
      const shiftFrom = localToUtc(date, shift.start, timeZone);
      const shiftUntil = new Date(shiftFrom.getTime() + shift.durationMinutes * 60000);
      if (shiftUntil.getTime() <= now && shiftFrom.getTime() < absence.endsAt && shiftUntil.getTime() > absence.startsAt) candidates.push({ shift, shiftFrom, shiftUntil });
    }
  }
  candidates.sort((x, y) => x.shiftFrom - y.shiftFrom);
  const selected = candidates[0];
  check('S15 finds a completed CNC Mill shift that the callout overlapped', !!selected, { candidates: candidates.length });
  if (!selected) return;
  const created = await post('/api/execution/shifts/handoffs', { workCenterCode: 'WC-CNC-MILL', shiftCode: selected.shift.code, shiftFrom: selected.shiftFrom.toISOString(),
    shiftUntil: selected.shiftUntil.toISOString(), context: 'WW-S15 replay: open CNC-01 downtime and recorded absence for the incoming shift.', reason: 'S15 replay handoff' }, supervisor);
  const handoffId = Number(created.entityId);
  await post(`/api/execution/shifts/handoffs/${handoffId}/review`, { reason: 'S15 replay review' }, supervisor);
  const detail = await get(`/api/execution/shifts/handoffs/${handoffId}`, supervisor);
  const facts = detail.handoff?.facts ?? detail.handoff?.snapshot?.facts ?? [];
  const openCnc = facts.some(f => f.kind === 'Downtime' && String(f.reference).includes('CNC-01') && f.status === 'Open');
  const absenceFact = facts.some(f => f.kind === 'WorkerAvailability' && f.status === 'CallOut');
  check('S15 outgoing handoff carries the open CNC-01 downtime and the recorded callout', openCnc && absenceFact,
    { handoffId, facts: facts.map(f => ({ kind: f.kind, reference: f.reference, status: f.status })) });
  const selfAck = await rejection(`/api/execution/shifts/handoffs/${handoffId}/acknowledge`, { note: 'S15 replay: creator tries', reason: 'S15 replay' }, supervisor);
  check('S15 refuses an acknowledgment by the handoff creator/reviewer', /independent_acknowledgment_required/.test(selfAck ?? ''), { selfAck });
  const people = await get('/api/kernel/people');
  const workers = await get('/api/workforce/workers', supervisor);
  const incomingWorker = workers.filter(w => w.isActive && w.homeWorkCenterCode === 'WC-CNC-MILL' && w.personId !== absence.personId)
    .sort((a, b) => a.displayName.localeCompare(b.displayName)).find(w => people.some(p => p.personId === w.personId && /^ww-operator-/.test(p.idpSubject)));
  const incoming = people.find(p => p.personId === incomingWorker?.personId);
  if (incoming) await post(`/api/execution/shifts/handoffs/${handoffId}/acknowledge`, { note: 'Received; downtime and absence reviewed (replay).', reason: 'S15 replay incoming acknowledgment' }, incoming.idpSubject);
  const after = await get(`/api/execution/shifts/handoffs/${handoffId}`, supervisor);
  evidence.s15 = { handoffId, shift: selected.shift.code, shiftFrom: selected.shiftFrom, incoming: incomingWorker?.displayName ?? null, selfAck };
  check('S15 a different incoming CNC Mill operator acknowledges the reviewed handoff', !!incoming
    && (after.acknowledgments ?? []).some(x => String(x.personId).toLowerCase() === String(incoming.personId).toLowerCase()), { acknowledgments: after.acknowledgments ?? [] });
}

async function replayS02(simulatedNow) {
  // Mirrors the Scenario Center's source workflow: three audited callout windows, two scheduled
  // workers without presence, and no fabricated verified attendance/no-show classification.
  const supervisor = 'ww-demo-supervisor';
  const workersBefore = await get('/api/workforce/workers', supervisor);
  const { callouts, missing } = selectDayShiftScenarioRoster(workersBefore);
  const selectedIds = [...callouts, ...missing].map(worker => worker.personId);
  if (new Set(selectedIds).size !== 5 || callouts.length !== 3 || missing.length !== 2)
    throw new Error('S02 requires five distinct active Day Shift workers; no absence events were recorded.');

  const startsAt = new Date(simulatedNow);
  const endsAt = new Date(startsAt.getTime() + 8 * 60 * 60 * 1000);
  const from = new Date(startsAt.getTime() - 60 * 60 * 1000).toISOString();
  const to = new Date(endsAt.getTime() + 60 * 60 * 1000).toISOString();
  const existing = await get(`/api/workforce/unavailability?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`, supervisor);

  for (const worker of callouts) {
    const overlapping = existing.find(item => item.personId === worker.personId
      && Date.parse(item.startsAt) < endsAt.getTime() && Date.parse(item.endsAt) > startsAt.getTime());
    if (overlapping && overlapping.kind !== 'CallOut')
      throw new Error(`S02 refused to replace ${overlapping.kind} unavailability for ${worker.personId}.`);
    if (!overlapping) await post(`/api/workforce/workers/${worker.personId}/unavailability`, {
      kind: 'CallOut', startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString(), reason: 'S02 replay: reported Day Shift callout',
    }, supervisor, `ww-replay-s02-callout-${worker.personId}`);
    if (worker.presentUntil && Date.parse(worker.presentUntil) > startsAt.getTime())
      await post(`/api/workforce/workers/${worker.personId}/departure`, { reason: 'S02 replay: record callout departure' },
        supervisor, `ww-replay-s02-departure-${worker.personId}`);
  }
  for (const worker of missing) {
    const overlapping = existing.find(item => item.personId === worker.personId
      && Date.parse(item.startsAt) < endsAt.getTime() && Date.parse(item.endsAt) > startsAt.getTime());
    if (overlapping) throw new Error(`S02 requires an unconfirmed absence, but ${worker.personId} already has ${overlapping.kind} unavailability.`);
    if (worker.presentUntil && Date.parse(worker.presentUntil) > startsAt.getTime())
      await post(`/api/workforce/workers/${worker.personId}/departure`, { reason: 'S02 replay: leave scheduled worker unconfirmed' },
        supervisor, `ww-replay-s02-departure-${worker.personId}`);
  }

  const [workersAfter, recorded] = await Promise.all([
    get('/api/workforce/workers', supervisor),
    get(`/api/workforce/unavailability?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`, supervisor),
  ]);
  const stateById = new Map(workersAfter.map(worker => [worker.personId, worker]));
  const windowById = personId => recorded.filter(item => item.personId === personId
    && Date.parse(item.startsAt) < endsAt.getTime() && Date.parse(item.endsAt) > startsAt.getTime());
  const calloutWindows = callouts.flatMap(worker => windowById(worker.personId).filter(item => item.kind === 'CallOut'));
  const unexpectedCalloutWindows = missing.flatMap(worker => windowById(worker.personId).filter(item => item.kind === 'CallOut'));
  const allFiveNotPresent = selectedIds.every(id => {
    const state = stateById.get(id);
    return !!state && (!state.presentUntil || Date.parse(state.presentUntil) <= startsAt.getTime());
  });
  const missingRemainScheduledAndUnconfirmed = missing.every(worker => {
    const state = stateById.get(worker.personId);
    return !!state && state.isActive && state.shiftCode === 'SHIFT-DAY' && !state.unavailability
      && (!state.presentUntil || Date.parse(state.presentUntil) <= startsAt.getTime());
  });
  const attendanceEvents = await get(`/api/workforce/attendance-intelligence/events?from=${encodeURIComponent(startsAt.toISOString())}&to=${encodeURIComponent(endsAt.toISOString())}`, supervisor);
  const recentAttendance = attendanceEvents.filter(event => selectedIds.includes(event.personId)
    && Date.parse(event.recordedAt) >= startsAt.getTime());
  const missingAttendanceClassifications = recentAttendance.filter(event => missing.some(worker => worker.personId === event.personId)
    && ['CallOut', 'NoShow'].includes(event.kind));
  const nonPendingCalloutClassifications = recentAttendance.filter(event => callouts.some(worker => worker.personId === event.personId)
    && event.kind === 'CallOut' && event.reviewState !== 'PendingReview');

  evidence.s02 = {
    startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString(),
    calloutWorkers: callouts.map(worker => ({ personId: worker.personId, name: worker.displayName })),
    unconfirmedWorkers: missing.map(worker => ({ personId: worker.personId, name: worker.displayName })),
    calloutWindows: calloutWindows.map(item => ({ personId: item.personId, kind: item.kind, startsAt: item.startsAt, endsAt: item.endsAt })),
    attendanceEvents: recentAttendance.map(event => ({ personId: event.personId, kind: event.kind, reviewState: event.reviewState })),
  };
  check('S02 selects three callouts and two other active Day Shift workers', new Set(selectedIds).size === 5
    && callouts.length === 3 && missing.length === 2 && [...callouts, ...missing].every(worker => worker.isActive && worker.shiftCode === 'SHIFT-DAY'),
  { calloutCount: callouts.length, unconfirmedCount: missing.length, distinctWorkers: new Set(selectedIds).size });
  check('S02 records exactly three current, audited CallOut windows', calloutWindows.length === 3
    && callouts.every(worker => windowById(worker.personId).filter(item => item.kind === 'CallOut').length === 1), evidence.s02);
  check('S02 ends presence for all five affected workers', allFiveNotPresent, { affected: selectedIds.map(id => ({ personId: id, presentUntil: stateById.get(id)?.presentUntil ?? null })) });
  check('S02 leaves two scheduled workers unconfirmed without callout or no-show records', missingRemainScheduledAndUnconfirmed
    && unexpectedCalloutWindows.length === 0 && missingAttendanceClassifications.length === 0,
  { workers: missing.map(worker => stateById.get(worker.personId)), unexpectedCallouts: unexpectedCalloutWindows, attendanceClassifications: missingAttendanceClassifications });
  check('S02 reported callouts remain pending independent attendance review', nonPendingCalloutClassifications.length === 0,
    { calloutClassifications: recentAttendance.filter(event => callouts.some(worker => worker.personId === event.personId) && event.kind === 'CallOut') });
}

const lotQuantity = (positions, partNumber, lotNumber, status, location = 'WW-RAW') => positions
  .filter(p => p.partNumber === partNumber && p.lotNumber === lotNumber && p.qualityStatus === status && (!location || p.locationCode === location))
  .reduce((sum, p) => sum + Number(p.quantity), 0);

// S04, S06, S08 and S11 through the same audited commands the Scenario Center uses, each read back from its source.
async function replayFloorEvents(siteToday) {
  // S04: CNC-01 unplanned downtime makes its Equipment readiness fail.
  const equipmentBefore = await machineDimension('CNC-01', 'CNC_MILL', 'Equipment');
  await post('/api/maintenance/machines/CNC-01/down', { description: 'Fictional demo event: unplanned spindle coolant failure', reason: 'S04 replay' }, 'ww-demo-supervisor');
  const now = Date.now();
  const downtime = await get(`/api/maintenance/downtime?from=${encodeURIComponent(new Date(now - 864e5).toISOString())}&to=${encodeURIComponent(new Date(now + 864e5).toISOString())}`, engineer);
  const open = downtime.filter(d => d.machineCode === 'CNC-01' && !d.endsAt);
  const equipmentAfter = await machineDimension('CNC-01', 'CNC_MILL', 'Equipment');
  evidence.s04 = { equipmentBefore, equipmentAfter, openDowntime: open.length };
  check('S04 records one open CNC-01 downtime', open.length === 1, { open });
  check('S04 CNC-01 equipment readiness fails while it is down', equipmentAfter.outcome === 'Fail' && equipmentBefore.outcome !== 'Fail', { equipmentBefore, equipmentAfter });

  // S06: the exact 500-unit motor lot moves from Released to Hold; nothing else changes.
  const motorBefore = await get('/api/inventory/positions', engineer);
  const releasedBefore = lotQuantity(motorBefore, 'WW-MOT-24V-DC', 'LOT-MOT-24V-881', 'Released');
  const heldBefore = lotQuantity(motorBefore, 'WW-MOT-24V-DC', 'LOT-MOT-24V-881', 'Hold');
  await post('/api/inventory/holds', { partNumber: 'WW-MOT-24V-DC', quantity: 500 - heldBefore, locationCode: 'WW-RAW', lotNumber: 'LOT-MOT-24V-881', status: 'Released', reason: 'S06 replay: suspect motor lot' }, engineer);
  const motorAfter = await get('/api/inventory/positions', engineer);
  const held = lotQuantity(motorAfter, 'WW-MOT-24V-DC', 'LOT-MOT-24V-881', 'Hold');
  const released = lotQuantity(motorAfter, 'WW-MOT-24V-DC', 'LOT-MOT-24V-881', 'Released');
  evidence.s06 = { releasedBefore, heldBefore, releasedAfter: released, heldAfter: held };
  check('S06 holds exactly 500 units of LOT-MOT-24V-881', held === 500 && released === releasedBefore - (500 - heldBefore), evidence.s06);

  // S08: 250 m sisal parent splits into 100/100/45 m child lots and the 5 m remnant is scrapped.
  const children = ['LOT-SISAL-M01-A', 'LOT-SISAL-M01-B', 'LOT-SISAL-M01-C'];
  const sisalBefore = await get('/api/inventory/positions', engineer);
  const parentBefore = lotQuantity(sisalBefore, 'WW-SISAL-SPOOL-250M', 'LOT-SISAL-M01', 'Released');
  check('S08 starts from one 250 m released sisal parent lot', parentBefore === 250, { parentBefore });
  if (parentBefore === 250) {
    await post('/api/inventory/splits', { partNumber: 'WW-SISAL-SPOOL-250M', lotNumber: 'LOT-SISAL-M01', locationCode: 'WW-RAW', status: 'Released',
      results: [{ lotNumber: children[0], quantity: 100 }, { lotNumber: children[1], quantity: 100 }, { lotNumber: children[2], quantity: 45 }], reason: 'S08 replay: cut to order lengths' }, engineer);
    await post('/api/inventory/scrap', { partNumber: 'WW-SISAL-SPOOL-250M', quantity: 5, locationCode: 'WW-RAW', lotNumber: 'LOT-SISAL-M01', status: 'Released', reason: 'S08 replay: 5 m remnant' }, engineer);
  }
  const sisalAfter = await get('/api/inventory/positions', engineer);
  const childQuantities = children.map(lot => lotQuantity(sisalAfter, 'WW-SISAL-SPOOL-250M', lot, 'Released'));
  const parentAfter = lotQuantity(sisalAfter, 'WW-SISAL-SPOOL-250M', 'LOT-SISAL-M01', 'Released');
  evidence.s08 = { parentBefore, parentAfter, children: Object.fromEntries(children.map((lot, i) => [lot, childQuantities[i]])) };
  check('S08 child lots are 100/100/45 m and the parent is consumed', childQuantities.join('/') === '100/100/45' && parentAfter === 0, evidence.s08);

  // S11: fictional past-due calibrations for the torque and optical gauges, read back from Quality.
  const calibratedOn = new Date(Date.parse(`${siteToday}T12:00:00Z`) - 2 * 864e5).toISOString().slice(0, 10);
  const dueDate = new Date(Date.parse(`${siteToday}T12:00:00Z`) - 864e5).toISOString().slice(0, 10);
  for (const code of ['GAUGE-TRQ-04', 'GAUGE-OPT-01'])
    await post(`/api/quality/gauges/${code}/calibrations`, { calibratedOn, dueDate, certificate: 'WW-DEMO-EXPIRED-CALIBRATION', reason: 'S11 replay: past-due calibration' }, engineer);
  const gauges = (await get('/api/quality/gauges', engineer)).filter(g => ['GAUGE-TRQ-04', 'GAUGE-OPT-01'].includes(g.code));
  evidence.s11 = { siteToday, gauges: gauges.map(g => ({ code: g.code, calibrationDue: g.calibrationDue })) };
  check('S11 torque and optical gauges are past due at the site date', gauges.length === 2 && gauges.every(g => g.calibrationDue < siteToday), evidence.s11);
}

async function main() {
  await ready();
  let status = await get('/api/demo/status');
  if (!status.enabled) throw new Error('The target host is not in demo mode (Forge:Demo:Enabled); nothing was written.');
  step('Demo host reachable', { simulatedNow: status.simulatedNow, designated: status.designated });

  if (!status.designated) {
    const sites = (await get('/api/resources/')).sites ?? [];
    if (sites.some(site => site.code !== 'WW-P01')) throw new Error(`Refusing: the target database holds other sites (${sites.map(s => s.code).join(', ')}).`);
    await ensureReader();
    // The foundation refuses to run again once the commercial layer exists; the later seeds are safe to rerun.
    if ((await get('/api/engineering/items', engineer)).length === 0) seed('seed-whiskerworks.mjs', [`--subject=${admin}`, `--reader=${engineer}`]);
    else step('Foundation already seeded');
    seed('seed-whiskerworks-commercial.mjs', [`--subject=${admin}`, `--reader=${planner}`]);
    seed('seed-whiskerworks-sprint10-competency.mjs', [`--subject=${admin}`, `--reader=${engineer}`]);
    const policies = await get('/api/workforce/attendance-policies');
    if (policies.some(policy => policy.policyCode === 'WW-S10-ATTENDANCE')) step('Attendance history already seeded');
    else seed('seed-whiskerworks-sprint10-attendance.mjs', [`--admin=${admin}`, '--reader=ww-demo-security', '--importer=ww-demo-attendance-importer', '--reviewer=ww-demo-supervisor']);
    await post('/api/demo/designate', { demoCode: 'WW-DEMO', siteCode: 'WW-P01', reason: 'Isolated WhiskerWorks replay database' });
    step('Designated WW-DEMO / WW-P01');
    status = await get('/api/demo/status');
  }

  if (!status.baseline) {
    step('Saving baseline (whole-database copy)');
    await post('/api/demo/baseline', { reason: 'S01 clean WhiskerWorks baseline' }, planner);
    step('Saved baseline');
  } else {
    step('Resetting to saved baseline');
    await post('/api/demo/reset', { reason: 'Replay from the saved baseline' });
    step('Reset to saved baseline');
  }

  status = await get('/api/demo/status');
  const baselineOffset = status.offsetSeconds;
  const baselineKpis = await get('/api/demo/kpis', planner);
  evidence.baseline = { status, kpis: baselineKpis };
  const sites = (await get('/api/resources/')).sites ?? [];
  check('Only the WW-P01 site exists in the replay database', sites.length === 1 && sites[0].code === 'WW-P01', { sites: sites.map(s => s.code) });
  check('The S01 baseline has released work', baselineKpis.releasedOrders > 0, { releasedOrders: baselineKpis.releasedOrders });

  await replayS03(status.simulatedNow); // before S02, whose callouts may include the ASM-03 crew
  await replayS02(status.simulatedNow);
  await replayS07();
  const siteZone = sites[0]?.timeZone ?? 'UTC';
  await replayFloorEvents(new Intl.DateTimeFormat('en-CA', { timeZone: siteZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(Date.parse(status.simulatedNow))));
  const resourcesAll = await get('/api/resources/');
  await replayS14(status.simulatedNow, resourcesAll);
  const s15Absence = await replayS15Absence(status.simulatedNow);

  // S05: a CNC-02 mill qualification seeded to expire five days out stops counting once the simulated clock passes it.
  const before = await cncMillReadiness();
  const workers = await get('/api/workforce/workers', engineer);
  const ids = workers.map(w => w.personId);
  const records = ids.length ? await get(`/api/competency/people/qualifications?${ids.map(id => `personIds=${encodeURIComponent(id)}`).join('&')}`, engineer) : [];
  const expiring = records.filter(r => r.code === scenarioQualificationExpiry.qualificationCode && r.expiresAt
    && Date.parse(r.expiresAt) - Date.parse(status.simulatedNow) < 7 * 864e5 && Date.parse(r.expiresAt) > Date.parse(status.simulatedNow));
  check('S05 seeds a CNC mill qualification expiring within a week', expiring.length >= 1, { expiring: expiring.map(r => ({ personId: r.personId, expiresAt: r.expiresAt })) });
  const person = workers.find(w => w.personId === expiring[0]?.personId);
  await post('/api/demo/clock/advance', { minutes: 6 * 24 * 60, reason: 'S05: six days later' }, planner);
  const later = await get('/api/demo/status');
  await replayS15Handoff(later.simulatedNow, resourcesAll, s15Absence);
  const qualificationsAfter = ids.length
    ? await get(`/api/competency/people/qualifications?${ids.map(id => `personIds=${encodeURIComponent(id)}`).join('&')}`, engineer)
    : [];
  const targetQualificationAfter = qualificationsAfter.find(record => record.personId === expiring[0]?.personId
    && record.code === scenarioQualificationExpiry.qualificationCode && record.expiresAt);
  const qualificationExpiredAtSimulatedTime = !!targetQualificationAfter
    && Date.parse(targetQualificationAfter.expiresAt) <= Date.parse(later.simulatedNow);
  const after = await cncMillReadiness();
  evidence.s05 = {
    person: person?.displayName ?? null,
    qualification: targetQualificationAfter ? { code: targetQualificationAfter.code, expiresAt: targetQualificationAfter.expiresAt } : null,
    qualificationExpiredAtSimulatedTime,
    before,
    after,
    simulatedNow: later.simulatedNow,
  };
  check('The simulated clock moved six days', later.offsetSeconds - baselineOffset === 6 * 24 * 3600, { offsetSeconds: later.offsetSeconds });
  check('The persisted S05 mill qualification expired at simulated time', qualificationExpiredAtSimulatedTime,
    { personId: expiring[0]?.personId ?? null, qualification: targetQualificationAfter ?? null, simulatedNow: later.simulatedNow });
  check('CNC-02 mill readiness no longer counts the operator after the clock advance',
    !!person && !(after.detail ?? '').includes(person.displayName), { detail: after.detail });

  evidence.comparison = await get('/api/demo/compare', planner);
  step('Compared with baseline');

  // A reset returns to the baseline exactly, clock included; the replaced state is archived, not deleted.
  const reset = await post('/api/demo/reset', { reason: 'Return to the clean baseline after the replay' });
  const restored = await get('/api/demo/status');
  const restoredKpis = await get('/api/demo/kpis', planner);
  check('Reset restores the baseline clock', restored.offsetSeconds === baselineOffset, { offsetSeconds: restored.offsetSeconds });
  check('Reset restores the baseline orders and stock', restoredKpis.releasedOrders === baselineKpis.releasedOrders
    && restoredKpis.heldOrders === baselineKpis.heldOrders && restoredKpis.stockHold === baselineKpis.stockHold, { restoredKpis });
  check('Reset archives the replaced database', /_archive_/.test(reset.message ?? ''), { message: reset.message });

  evidence.finishedAt = new Date().toISOString();
  evidence.passed = evidence.assertions.every(a => a.passed);
  writeFileSync(out, JSON.stringify(evidence, null, 2));
  console.log(`${evidence.assertions.filter(a => a.passed).length}/${evidence.assertions.length} assertions passed. Evidence: ${out}`);
  if (!evidence.passed) process.exitCode = 1;
}

main().catch(error => {
  evidence.error = error.cause ? `${error.message}: ${error.cause.code ?? ''} ${error.cause.message ?? error.cause}` : error.message;
  evidence.passed = false;
  try { writeFileSync(out, JSON.stringify(evidence, null, 2)); } catch { /* ignore */ }
  console.error(evidence.error);
  process.exitCode = 1;
});
