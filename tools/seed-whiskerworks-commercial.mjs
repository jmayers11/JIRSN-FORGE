#!/usr/bin/env node
// Adds the fictional WhiskerWorks engineering, customer-demand, inventory and order layer
// to a separately hosted demo database whose WW-P01 workforce foundation is already seeded.
// It is dry-run by default. There is no reset or delete command.
import { createHash } from 'node:crypto';
import { site, workCenters, machines, nonMachineStations } from './whiskerworks-plant.mjs';
import {
  allItems, components, customers, operationalLocations, products, subassemblyBoms,
  subassemblyRoutes, suppliers, scenarioCatalog, createDemoWorkload,
} from './whiskerworks-commercial.mjs';

const args = process.argv.slice(2);
const value = name => args.find(argument => argument.startsWith(`--${name}=`))?.slice(name.length + 3);
const apply = args.includes('--apply');
const baseArg = value('base');
const subject = value('subject');
const reader = value('reader');
const asOf = value('as-of') ?? '2026-10-26';
const base = (baseArg ?? 'http://localhost:5080').replace(/\/+$/, '');
const reason = 'Seed versioned fictional WhiskerWorks commercial demo data';
const idempotencyPrefix = 'ww-commercial-v1-';
const wwPartCodes = new Set(allItems.map(item => item.partNumber));
const wwSupplierCodes = new Set(suppliers.map(item => item.code));
const wwLocationCodes = new Set(operationalLocations.map(item => item.code));
const wwDocuments = new Set(['WI-CNC-101', 'WI-ELE-610', 'WI-ASM-400']);
const sensorSupply = { partNumber: 'WW-SENS-PROX-05', supplierCode: 'WW-SUP-009', quantity: 100, unitPrice: 18.5, currency: 'USD' };
const bearingSupply = { partNumber: 'WW-BRG-608-2RS', supplierCode: 'WW-SUP-010', quantity: 120, unitPrice: 4.25, currency: 'USD' };
const sensorMpn = { manufacturer: 'Quiet Current Controls', mpn: 'QCC-PX-500', manufacturerRevision: 'A', lifecycle: 'Active', datasheet: null, note: 'Fictional WhiskerWorks demo manufacturer part.' };
const knownBoms = [
  ...products.map(product => ({ partNumber: product.partNumber, lines: product.bom })),
  ...subassemblyBoms,
];
const knownRoutes = [
  ...products.map(product => ({ partNumber: product.partNumber, operations: product.route.map(([description, workCenterCode, activityCode, eligibleMachines, requiredQualifications, crewSize, runMinutesPerUnit], index) => ({
    sequence: (index + 1) * 10, description, workCenterCode, activityCode, eligibleMachines, requiredQualifications,
    setupMinutes: 5, runMinutesPerUnit, crewSize,
  })) })),
  ...subassemblyRoutes.map(item => ({ partNumber: item.partNumber, operations: [item.operation] })),
];

function validateCatalog() {
  const unique = (items, key, label) => {
    const keys = items.map(key);
    if (new Set(keys).size !== keys.length) throw new Error(`WhiskerWorks catalog repeats ${label}.`);
  };
  unique(allItems, item => item.partNumber, 'a part number');
  unique(suppliers, item => item.code, 'a supplier code');
  unique(operationalLocations, item => item.code, 'a location code');
  if (components.length !== 95 || allItems.length !== 100 || products.length !== 5 || suppliers.length !== 10 || customers.length !== 10 || scenarioCatalog.length !== 16) {
    throw new Error(`WhiskerWorks target counts do not match the catalog: ${components.length} components, ${allItems.length} total items, ${suppliers.length} suppliers, ${customers.length} customers, ${scenarioCatalog.length} scenarios.`);
  }
  const parts = new Set(allItems.map(item => item.partNumber));
  for (const bom of knownBoms) {
    if (!parts.has(bom.partNumber)) throw new Error(`BOM parent ${bom.partNumber} is not in the item catalog.`);
    for (const [part, quantity] of bom.lines) {
      if (!parts.has(part) || !Number.isFinite(quantity) || quantity <= 0) throw new Error(`BOM ${bom.partNumber} has an invalid component or quantity: ${part}.`);
    }
  }
  for (const route of knownRoutes) {
    if (!parts.has(route.partNumber) || !route.operations.length) throw new Error(`Routing ${route.partNumber} is incomplete.`);
    for (const operation of route.operations) {
      if (!workCenters.some(center => center.code === operation.workCenterCode)) throw new Error(`Unknown route center ${operation.workCenterCode}.`);
      if (operation.eligibleMachines.some(code => !machines.some(machine => machine.code === code))) throw new Error(`Unknown machine in ${route.partNumber} routing.`);
      if (operation.crewSize < 1 || operation.runMinutesPerUnit <= 0) throw new Error(`Invalid standard in ${route.partNumber} routing.`);
    }
  }
  const bomByParent = new Map(knownBoms.map(bom => [bom.partNumber, bom.lines.map(([part]) => part)]));
  const active = new Set();
  const visited = new Set();
  const visit = part => {
    if (active.has(part)) throw new Error(`WhiskerWorks BOM cycle detected at ${part}.`);
    if (visited.has(part)) return;
    active.add(part);
    for (const child of bomByParent.get(part) ?? []) if (bomByParent.has(child)) visit(child);
    active.delete(part);
    visited.add(part);
  };
  for (const part of bomByParent.keys()) visit(part);
  const makeParts = new Set(allItems.filter(item => item.makeBuy === 'Make').map(item => item.partNumber));
  if (knownBoms.length !== makeParts.size || knownRoutes.length !== makeParts.size || [...makeParts].some(part => !bomByParent.has(part)))
    throw new Error('Every WhiskerWorks make item must have exactly one BOM and routing.');
  const workload = createDemoWorkload(asOf);
  unique(workload.productionOrders, order => `${order.partNumber}|${order.dueDate}|${order.quantity}|${order.priority}`, 'a production-order signature');
}
validateCatalog();

if (!apply) {
  const workload = createDemoWorkload(asOf);
  console.log('WhiskerWorks commercial seed plan (dry run; no server or database calls made)');
  console.log(`Target: ${site.code} · ${site.name}`);
  console.log(`Catalog: ${allItems.length} items (${components.length} components + ${products.length} finished products), ${knownBoms.length} revision-controlled BOMs, ${knownRoutes.length} routings.`);
  console.log(`Commercial workload: ${suppliers.length} suppliers, ${customers.length} fictional demand customers, approved open sensor and bearing supply lines, ${workload.demandLines.length} open demand lines, ${workload.productionOrders.length} planned/released/held production orders.`);
  console.log(`Material setup: ${operationalLocations.length} locations. Stable demo as-of date: ${asOf}.`);
  console.log(`Maintenance setup: ${machines.length} capacity-bearing machine assets plus ${nonMachineStations.length} fixed support-station assets without machine capacity.`);
  console.log('To apply: pass --apply --base=http://localhost:<port> --subject=<authorized-admin> --reader=<broad-read-user> --as-of=YYYY-MM-DD.');
  console.log('The apply path refuses an unrelated site or any non-WhiskerWorks business records. It has no reset/delete operation.');
  process.exit(0);
}

if (!baseArg || !subject || !reader) throw new Error('Applying requires explicit --base, --subject and --reader arguments.');
const target = new URL(base);
if (!['localhost', '127.0.0.1', '::1'].includes(target.hostname) && !args.includes('--allow-remote-demo')) {
  throw new Error('Refusing a non-local target. Use --allow-remote-demo only for a deliberately isolated demo server.');
}
if (!/^\d{4}-\d{2}-\d{2}$/.test(asOf) || Number.isNaN(Date.parse(`${asOf}T00:00:00Z`))) throw new Error('--as-of must be a valid YYYY-MM-DD date.');

async function request(method, path, who = subject, payload = undefined, stableKey = '') {
  const headers = { 'X-Forge-Dev-Subject': who, Accept: 'application/json' };
  let body;
  if (payload !== undefined) {
    body = { reason, ...payload };
    headers['Content-Type'] = 'application/json';
    const hash = createHash('sha256').update(`${stableKey}\n${method}\n${path}\n${JSON.stringify(body)}`).digest('hex').slice(0, 32);
    headers['Idempotency-Key'] = `${idempotencyPrefix}${hash}`;
  }
  const response = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} as ${who} returned ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const get = (path, who = reader) => request('GET', path, who);
const post = (path, body, who = subject, stableKey = '') => request('POST', path, who, body, stableKey);
async function postFile(path, fileName, content, who = subject) {
  const bytes = Buffer.from(content, 'utf8');
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  const stableKey = `file-${sha256}`;
  const key = createHash('sha256').update(`${stableKey}\nPOST\n${path}\n${sha256}`).digest('hex').slice(0, 32);
  const response = await fetch(`${base}${path}`, { method: 'POST', headers: {
    'X-Forge-Dev-Subject': who, Accept: 'application/json', 'Content-Type': 'text/plain; charset=utf-8',
    'X-File-Name': encodeURIComponent(fileName), 'Idempotency-Key': `${idempotencyPrefix}${key}`,
  }, body: bytes });
  const text = await response.text();
  if (!response.ok) throw new Error(`POST ${path} as ${who} returned ${response.status}: ${text}`);
  return { sha256, result: text ? JSON.parse(text) : null };
}
const addDays = (date, days) => new Date(Date.parse(`${date}T12:00:00.000Z`) + days * 86_400_000).toISOString().slice(0, 10);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const requireOnly = (label, rows, allowed, code = row => row.code) => {
  const foreign = rows.filter(row => !allowed.has(code(row)));
  if (foreign.length) throw new Error(`Refusing to mix WhiskerWorks with existing ${label}: ${foreign.slice(0, 5).map(code).join(', ')}.`);
};
const checkSame = (label, code, expected, actual) => {
  if (!same(expected, actual)) throw new Error(`Existing ${label} ${code} differs from the versioned demo definition; no overwrite was attempted.`);
};
const machineAssetDefinition = machine => ({
  assetCode: machine.code, name: machine.name, type: 'Production', manufacturer: 'WhiskerWorks Demo Equipment Works',
  model: `WW-${machine.code}`, serialNumber: `WW-SERIAL-${machine.code}`, location: machine.workCenterCode,
  workCenterCode: machine.workCenterCode, machineCode: machine.code, criticality: 'High', expectedLifeYears: 10, replacementCost: 12000,
});
const stationAssetDefinition = station => ({
  assetCode: station.code, name: station.name, type: station.type, manufacturer: 'WhiskerWorks Demo Facilities',
  model: `WW-FIXTURE-${station.code}`, serialNumber: `WW-SERIAL-${station.code}`, location: station.code,
  workCenterCode: station.workCenterCode, machineCode: null,
  criticality: station.type === 'Utility' || station.type === 'TestEquipment' ? 'High' : station.type === 'Building' ? 'Medium' : 'Low',
  expectedLifeYears: 15, replacementCost: 2500,
});
const validateAssetPreflight = existingAssets => {
  const existingByCode = new Map(existingAssets.map(asset => [asset.assetCode, asset]));
  const definitions = [...machines.map(machineAssetDefinition), ...nonMachineStations.map(stationAssetDefinition)];
  for (const expected of definitions) {
    const current = existingByCode.get(expected.assetCode);
    if (!current) continue;
    for (const key of ['name', 'type', 'manufacturer', 'model', 'serialNumber', 'location', 'workCenterCode', 'machineCode', 'criticality', 'expectedLifeYears', 'replacementCost'])
      if (current[key] !== expected[key]) throw new Error(`Existing WhiskerWorks asset ${expected.assetCode} differs on ${key}; no records were changed.`);
    if (!['Planned', 'Active'].includes(current.status))
      throw new Error(`Existing WhiskerWorks asset ${expected.assetCode} is ${current.status}; no records were changed.`);
  }
};

async function main() {
  await get('/health/ready', subject).catch(error => { throw new Error(`Forge is not ready at ${base}: ${error.message}`); });
  const profile = await get('/api/kernel/me', reader);
  const required = ['engineering.read', 'execution.orders.read', 'inventory.read', 'procurement.read', 'quality.read', 'maintenance.read', 'planning.demand.read', 'programs.directory'];
  const permissions = new Set(profile.permissions ?? []);
  const missing = required.filter(permission => !permissions.has(permission));
  if (missing.length) throw new Error(`Preflight reader ${reader} is missing required read permissions: ${missing.join(', ')}.`);
  const [buyerProfile, approverProfile] = await Promise.all([
    get('/api/kernel/me', 'ww-demo-engineer'), get('/api/kernel/me', 'ww-demo-certifier'),
  ]);
  const buyerActor = `person:${buyerProfile.personId}`;
  const approverActor = `person:${approverProfile.personId}`;

  const [resourceState, existingItems, existingSuppliers, existingLocations, existingDemand, existingOrders, existingPlans, existingGauges, existingAssets, existingPmPlans, existingDocuments, programDirectory, existingPurchaseOrders, existingApprovedSources, existingBearingSources, existingSensorMpns] = await Promise.all([
    get('/api/resources/?includeRetired=true', subject), get('/api/engineering/items'), get('/api/procurement/suppliers'), get('/api/inventory/locations'),
    get('/api/planning/demand'), get('/api/orders/', subject), get('/api/quality/plans'), get('/api/quality/gauges'), get('/api/maintenance/assets'), get('/api/maintenance/plans'),
    get('/api/engineering/documents'), get('/api/programs/directory', subject),
    get('/api/procurement/purchase-orders', reader), get(`/api/procurement/approved-sources?part=${encodeURIComponent(sensorSupply.partNumber)}`, reader),
    get(`/api/procurement/approved-sources?part=${encodeURIComponent(bearingSupply.partNumber)}`, reader),
    get(`/api/engineering/manufacturer-parts?part=${encodeURIComponent(sensorSupply.partNumber)}`, reader),
  ]);
  if (!(resourceState.sites ?? []).some(item => item.code === site.code)) throw new Error(`The WhiskerWorks foundation is missing site ${site.code}; seed the foundation first.`);
  requireOnly('programs', programDirectory, new Set(['WW-DEMO']));
  requireOnly('engineering items', existingItems, wwPartCodes, item => item.partNumber);
  requireOnly('suppliers', existingSuppliers, wwSupplierCodes);
  for (const expected of suppliers) {
    const current = existingSuppliers.find(item => item.code === expected.code);
    if (current) checkSame('supplier', expected.code, { ...expected, status: 'Active' }, {
      code: current.code, name: current.name, notes: current.notes, status: current.status,
    });
  }
  requireOnly('inventory locations', existingLocations, wwLocationCodes);
  requireOnly('inspection plans', existingPlans, new Set(products.map(item => item.partNumber)), item => item.partNumber);
  requireOnly('quality gauges', existingGauges, new Set(['GAUGE-TRQ-04', 'GAUGE-OPT-01', 'GAUGE-CAL-01']), item => item.code);
  requireOnly('maintenance assets', existingAssets, new Set([...machines.map(item => item.code), ...nonMachineStations.map(item => item.code)]), item => item.assetCode);
  validateAssetPreflight(existingAssets);
  requireOnly('preventive-maintenance plans', existingPmPlans, new Set(machines.map(item => item.code)), item => item.machineCode);
  requireOnly('controlled documents', existingDocuments, wwDocuments, item => item.number);
  if (existingPurchaseOrders.some(order => order.lines?.some(line => !wwPartCodes.has(line.partNumber))))
    throw new Error('Refusing a purchase order containing a non-WhiskerWorks part; use a separate demo database.');
  const sensorPos = existingPurchaseOrders.filter(order => order.supplierCode === sensorSupply.supplierCode
    && order.lines?.some(line => line.partNumber === sensorSupply.partNumber));
  const bearingPos = existingPurchaseOrders.filter(order => order.supplierCode === bearingSupply.supplierCode
    && order.lines?.some(line => line.partNumber === bearingSupply.partNumber));
  if (sensorPos.length > 1) throw new Error('More than one WhiskerWorks sensor supply order exists; no records were changed.');
  if (bearingPos.length > 1) throw new Error('More than one WhiskerWorks bearing supply order exists; no records were changed.');
  const originalSensorPromise = addDays(asOf, 10);
  const delayedSensorPromise = addDays(originalSensorPromise, 4);
  if (sensorPos.length === 1) {
    const [order] = sensorPos;
    const [line] = order.lines ?? [];
    const allowedDates = new Set([originalSensorPromise, delayedSensorPromise]);
    if (order.lines?.length !== 1 || !['Draft', 'Approved'].includes(order.status) || line.partNumber !== sensorSupply.partNumber
      || line.quantity !== sensorSupply.quantity || line.unitPrice !== sensorSupply.unitPrice || line.currency !== sensorSupply.currency
      || !allowedDates.has(line.promisedDate) || !['Open'].includes(line.status) || order.createdBy !== buyerActor
      || order.status === 'Approved' && order.approvedBy !== approverActor)
      throw new Error(`Existing WhiskerWorks sensor supply purchase order ${order.poNumber} differs from the versioned demo definition; no records were changed.`);
  }
  const originalBearingPromise = addDays(asOf, 12);
  const delayedBearingPromise = addDays(originalBearingPromise, 4);
  if (bearingPos.length === 1) {
    const [order] = bearingPos;
    const [line] = order.lines ?? [];
    const allowedDates = new Set([originalBearingPromise, delayedBearingPromise]);
    if (order.lines?.length !== 1 || !['Draft', 'Approved'].includes(order.status) || line.partNumber !== bearingSupply.partNumber
      || line.quantity !== bearingSupply.quantity || line.unitPrice !== bearingSupply.unitPrice || line.currency !== bearingSupply.currency
      || !allowedDates.has(line.promisedDate) || line.status !== 'Open' || order.createdBy !== buyerActor
      || order.status === 'Approved' && order.approvedBy !== approverActor)
      throw new Error(`Existing WhiskerWorks bearing supply purchase order ${order.poNumber} differs from the versioned demo definition; no records were changed.`);
  }
  requireOnly('approved sensor supplier sources', existingApprovedSources,
    new Set([`${sensorSupply.partNumber}|${sensorSupply.supplierCode}`]), item => `${item.partNumber}|${item.supplierCode}`);
  for (const source of existingApprovedSources) if (source.approvedBy !== approverActor)
    throw new Error(`Approved WhiskerWorks sensor source ${source.partNumber}/${source.supplierCode} is owned by another approver; no records were changed.`);
  requireOnly('approved bearing supplier sources', existingBearingSources,
    new Set([`${bearingSupply.partNumber}|${bearingSupply.supplierCode}`]), item => `${item.partNumber}|${item.supplierCode}`);
  for (const source of existingBearingSources) if (source.approvedBy !== approverActor)
    throw new Error(`Approved WhiskerWorks bearing source ${source.partNumber}/${source.supplierCode} is owned by another approver; no records were changed.`);
  requireOnly('approved sensor manufacturer parts', existingSensorMpns,
    new Set([`${sensorSupply.partNumber}|${sensorMpn.manufacturer}|${sensorMpn.mpn}`]), item => `${item.partNumber}|${item.manufacturer}|${item.mpn}`);
  if (existingSensorMpns.length === 1) {
    const [mpn] = existingSensorMpns;
    if (mpn.manufacturerRevision !== sensorMpn.manufacturerRevision || mpn.lifecycle !== sensorMpn.lifecycle
      || mpn.status !== 'Proposed' && mpn.status !== 'Approved' || mpn.proposedBy !== buyerActor
      || mpn.status === 'Approved' && mpn.approvedBy !== approverActor)
      throw new Error(`Existing sensor manufacturer part differs from the versioned demo definition; no records were changed.`);
  }
  if (existingDemand.some(item => !String(item.sourceKey ?? '').startsWith('WW-DEMO-DEMAND-'))) throw new Error('Refusing existing demand without a WhiskerWorks demo source key. Use a separate empty demo database.');
  const existingProgram = programDirectory.find(item => item.code === 'WW-DEMO');
  if (existingProgram && (existingProgram.name !== 'WhiskerWorks Fictional Demonstration' || existingProgram.status !== 'Active'))
    throw new Error('Existing WW-DEMO program differs from the active WhiskerWorks demo boundary.');
  if (existingProgram && existingOrders.some(order => order.programId !== existingProgram.programId)) throw new Error('Refusing production orders outside the WW-DEMO program.');
  if (existingOrders.length > 42 || existingDemand.length > 28) throw new Error('WhiskerWorks demo program contains more orders or demand lines than the versioned seed; no records were changed.');
  if (!existingProgram && existingOrders.length) throw new Error('Refusing to create WW-DEMO in a database with existing production orders.');
  for (const code of [...wwPartCodes]) {
    const current = existingItems.find(item => item.partNumber === code);
    const expected = allItems.find(item => item.partNumber === code);
    if (current && !same({ description: current.description, unitOfMeasure: current.unitOfMeasure, makeBuy: current.makeBuy, lotTracked: current.lotTracked, serialized: current.serialized, class: current.class }, {
      description: expected.description, unitOfMeasure: expected.unitOfMeasure, makeBuy: expected.makeBuy, lotTracked: expected.lotTracked, serialized: expected.serialized, class: expected.class,
    })) throw new Error(`Existing demo part ${code} differs from the current versioned catalog.`);
  }

  const program = existingProgram ?? (await post('/api/programs', { code: 'WW-DEMO', name: 'WhiskerWorks Fictional Demonstration', customer: 'Fictional demo customers', description: 'Program boundary for fictional WhiskerWorks demonstration data.' }, 'ww-demo-planner')).entityId;
  const programId = typeof program === 'string' ? program : program.programId;
  const people = await get('/api/kernel/people', subject);
  const wantedSubjects = new Set([subject, reader, 'ww-demo-engineer', 'ww-demo-certifier', 'ww-demo-supervisor', 'ww-demo-planner', 'ww-demo-inventory']);
  const peopleBySubject = new Map(people.map(person => [person.idpSubject, person]));
  for (const identity of wantedSubjects) {
    const person = peopleBySubject.get(identity);
    if (!person) throw new Error(`Required WhiskerWorks principal ${identity} is missing; seed the foundation first.`);
    const access = await get('/api/kernel/me', identity);
    if (!(access.programs ?? []).includes(programId)) await post(`/api/kernel/people/${person.personId}/programs`, { programId }, subject);
  }

  const itemsByCode = new Map(existingItems.map(item => [item.partNumber, item]));
  for (const item of allItems) {
    if (itemsByCode.has(item.partNumber)) continue;
    await post('/api/engineering/items', {
      partNumber: item.partNumber, description: item.description, unitOfMeasure: item.unitOfMeasure, makeBuy: item.makeBuy,
      leadTimeDays: item.leadTimeDays, lotTracked: item.lotTracked, serialized: item.serialized, class: item.class,
    }, 'ww-demo-engineer');
  }

  const workInstructions = [
    { number: 'WI-CNC-101', title: 'Fictional CNC setup work-instruction reference' },
    { number: 'WI-ELE-610', title: 'Fictional electronics assembly work-instruction reference' },
    { number: 'WI-ASM-400', title: 'Fictional heavy assembly work-instruction reference' },
  ];
  const documentRows = new Map(existingDocuments.map(item => [item.number, item]));
  for (const definition of workInstructions) {
    const fileName = `${definition.number}-FICTIONAL-REFERENCE.txt`;
    const file = await postFile('/api/engineering/document-files', fileName,
      `FICTIONAL / SAMPLE DATA — WhiskerWorks ${definition.number}\n\nThis is a demonstration placeholder only. It is not a manufacturing, inspection, assembly, or safety procedure. Obtain approved controlled instructions before performing work.\n`,
      'ww-demo-engineer');
    let current = documentRows.get(definition.number);
    if (current) checkSame('controlled document', definition.number,
      { title: definition.title, type: 'WorkInstruction', authority: 'Internal' },
      { title: current.title, type: current.type, authority: current.authority });
    else {
      await post('/api/engineering/documents', {
        number: definition.number, title: definition.title, type: 'WorkInstruction', authority: 'Internal',
        externalAuthority: null, externalNumber: null, programId: null, markings: 'None',
      }, 'ww-demo-engineer');
      current = { number: definition.number, title: definition.title, type: 'WorkInstruction', authority: 'Internal', revisions: [] };
      documentRows.set(definition.number, current);
    }
    const detail = await get(`/api/engineering/documents/${encodeURIComponent(definition.number)}`);
    const document = detail.document ?? current;
    let documentRevision = (document.revisions ?? []).find(revision => revision.revision === 'A');
    if (!documentRevision) {
      await post(`/api/engineering/documents/${encodeURIComponent(definition.number)}/revisions`, {
        revision: 'A', fileSha256: file.sha256, fileName,
        changeNote: 'Fictional demonstration placeholder. This record is not an approved manufacturing procedure.',
        reviewBy: addDays(asOf, 365),
      }, 'ww-demo-engineer');
      documentRevision = { revision: 'A', status: 'Draft' };
    }
    if (documentRevision.status !== 'Released' && documentRevision.fileSha256 !== file.sha256)
      await post(`/api/engineering/documents/${encodeURIComponent(definition.number)}/revisions/A`, {
        fileSha256: file.sha256, fileName,
        changeNote: 'Fictional demonstration placeholder. This record is not an approved manufacturing procedure.',
        reviewBy: addDays(asOf, 365),
      }, 'ww-demo-engineer');
    if (documentRevision.status === 'Released' && documentRevision.fileSha256 !== file.sha256)
      throw new Error(`Released document ${definition.number}/A does not match the fictional demo reference; no history was changed.`);
    if (documentRevision.status === 'Draft') await post(`/api/engineering/documents/${encodeURIComponent(definition.number)}/revisions/A/submit`, {}, 'ww-demo-engineer');
    if (documentRevision.status === 'Draft' || documentRevision.status === 'InReview') await post(`/api/engineering/documents/${encodeURIComponent(definition.number)}/revisions/A/approve`, {}, 'ww-demo-certifier');
    if (documentRevision.status !== 'Released') await post(`/api/engineering/documents/${encodeURIComponent(definition.number)}/revisions/A/release`, { effectiveFrom: null }, 'ww-demo-certifier');
  }

  const makeItems = allItems.filter(item => item.makeBuy === 'Make');
  for (const item of makeItems) {
    let revisions = await get(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions`);
    if (revisions.some(row => row.revision !== 'A')) throw new Error(`Part ${item.partNumber} already has a non-demo revision; no engineering history was changed.`);
    let revision = revisions.find(row => row.revision === 'A');
    if (!revision) {
      await post(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions`, { revision: 'A' }, 'ww-demo-engineer');
      revision = { revision: 'A', status: 'Draft' };
    }
    let detail = await get(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/A`);
    const bom = knownBoms.find(row => row.partNumber === item.partNumber);
    const currentBom = new Map((detail.bom ?? []).map(line => [line.lineNumber, line]));
    for (let index = 0; index < (bom?.lines ?? []).length; index++) {
        const [componentPartNumber, quantityPer, scrapPercent = 0] = bom.lines[index];
        const lineNumber = (index + 1) * 10;
        const existingLine = currentBom.get(lineNumber);
        if (existingLine) checkSame('BOM line', `${item.partNumber}/${lineNumber}`, { componentPartNumber, quantityPer, scrapPercent }, {
          componentPartNumber: existingLine.componentPartNumber, quantityPer: existingLine.quantityPer, scrapPercent: existingLine.scrapPercent,
        });
        else if (revision.status !== 'Released') await post(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/A/bom`, { lineNumber, componentPartNumber, quantityPer, scrapPercent }, 'ww-demo-engineer');
    }
    if (currentBom.size !== (bom?.lines ?? []).length) throw new Error(`Revision A BOM for ${item.partNumber} has an unexpected line; no change was made.`);
    const route = knownRoutes.find(row => row.partNumber === item.partNumber);
    const currentRouting = new Map((detail.routing ?? []).map(operation => [operation.sequence, operation]));
    for (const operation of route?.operations ?? []) {
        const existingOperation = currentRouting.get(operation.sequence);
        const comparable = {
          description: operation.description, workCenterCode: operation.workCenterCode, activityCode: operation.activityCode,
          eligibleMachines: operation.eligibleMachines, requiredQualifications: operation.requiredQualifications,
          setupMinutes: operation.setupMinutes, runMinutesPerUnit: operation.runMinutesPerUnit, crewSize: operation.crewSize,
        };
        if (existingOperation) checkSame('routing operation', `${item.partNumber}/${operation.sequence}`, comparable, {
          description: existingOperation.description, workCenterCode: existingOperation.workCenterCode, activityCode: existingOperation.activityCode,
          eligibleMachines: existingOperation.eligibleMachines, requiredQualifications: existingOperation.requiredQualifications,
          setupMinutes: existingOperation.setupMinutes, runMinutesPerUnit: existingOperation.runMinutesPerUnit, crewSize: existingOperation.crewSize,
        });
        else if (revision.status !== 'Released') await post(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/A/routing`, operation, 'ww-demo-engineer');
    }
    if (currentRouting.size !== (route?.operations ?? []).length) throw new Error(`Revision A routing for ${item.partNumber} has an unexpected operation; no change was made.`);
    const documentRequirement = new Map([
      ['WW-PCB-CTRL-200', ['WI-ELE-610', 10]],
      ['WW-CAT-1000', ['WI-CNC-101', 10]], ['WW-CAT-2000', ['WI-ELE-610', 20]],
      ['WW-CAT-3000', ['WI-ELE-610', 20]], ['WW-CAT-4000', ['WI-ASM-400', 40]],
      ['WW-CAT-5000', ['WI-ELE-610', 20]],
    ]).get(item.partNumber);
    if (documentRequirement) {
      const [documentNumber, operationSequence] = documentRequirement;
      const currentRequirements = await get(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/A/documents`);
      const linked = currentRequirements.some(requirement => requirement.documentNumber === documentNumber && requirement.documentRevision === 'A' && requirement.operationSequence === operationSequence);
      if (!linked && revision.status === 'Released') throw new Error(`Released revision ${item.partNumber}/A is missing controlled document ${documentNumber}; no silent edit was attempted.`);
      if (!linked) await post(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/A/documents`, {
        documentNumber, documentRevision: 'A', operationSequence,
        note: 'Fictional demo training reference; placeholder content is not an approved procedure.',
      }, 'ww-demo-engineer');
    }
    if (revision.status !== 'Released') await post(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/A/release`, { effectiveFrom: null }, 'ww-demo-certifier');
    const currentItem = itemsByCode.get(item.partNumber) ?? item;
    if (currentItem.leadTimeDays !== item.leadTimeDays || currentItem.safetyStock !== 0 || currentItem.lotPolicy !== 'LotForLot' || currentItem.orderQuantity !== 0 || currentItem.orderMultiple !== 0)
      await post(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/planning`, {
        leadTimeDays: item.leadTimeDays, safetyStock: 0, lotPolicy: 'LotForLot', orderQuantity: 0, orderMultiple: 0,
      }, 'ww-demo-engineer');
  }

  const suppliersByCode = new Map(existingSuppliers.map(item => [item.code, item]));
  for (const supplier of suppliers) {
    const current = suppliersByCode.get(supplier.code);
    if (!current) await post('/api/procurement/suppliers', supplier, 'ww-demo-engineer');
  }

  const approvedSensorSource = existingApprovedSources.find(source => source.partNumber === sensorSupply.partNumber && source.supplierCode === sensorSupply.supplierCode);
  if (!approvedSensorSource) await post(`/api/procurement/items/${encodeURIComponent(sensorSupply.partNumber)}/approved-sources`, {
    supplierCode: sensorSupply.supplierCode, note: 'Fictional WhiskerWorks demo sourcing relationship.',
  }, 'ww-demo-certifier', `approved-source-${sensorSupply.partNumber}-${sensorSupply.supplierCode}`);
  const approvedBearingSource = existingBearingSources.find(source => source.partNumber === bearingSupply.partNumber && source.supplierCode === bearingSupply.supplierCode);
  if (!approvedBearingSource) await post(`/api/procurement/items/${encodeURIComponent(bearingSupply.partNumber)}/approved-sources`, {
    supplierCode: bearingSupply.supplierCode, note: 'Fictional WhiskerWorks demo sourcing relationship.',
  }, 'ww-demo-certifier', `approved-source-${bearingSupply.partNumber}-${bearingSupply.supplierCode}`);
  let manufacturerPart = existingSensorMpns[0];
  if (!manufacturerPart) {
    await post(`/api/engineering/items/${encodeURIComponent(sensorSupply.partNumber)}/manufacturer-parts`, sensorMpn, 'ww-demo-engineer', `sensor-mpn-${sensorMpn.mpn}`);
    manufacturerPart = { ...sensorMpn, partNumber: sensorSupply.partNumber, status: 'Proposed' };
  }
  if (manufacturerPart.status === 'Proposed') await post(`/api/engineering/items/${encodeURIComponent(sensorSupply.partNumber)}/manufacturer-parts/approve`, {
    manufacturer: sensorMpn.manufacturer, mpn: sensorMpn.mpn,
  }, 'ww-demo-certifier', `approve-sensor-mpn-${sensorMpn.mpn}`);
  let sensorOrder = sensorPos[0];
  if (!sensorOrder) {
    const created = await post('/api/procurement/purchase-orders', { supplier: sensorSupply.supplierCode, lines: [{
      partNumber: sensorSupply.partNumber, quantity: sensorSupply.quantity, promisedDate: originalSensorPromise,
      unitPrice: sensorSupply.unitPrice, currency: sensorSupply.currency,
    }] }, 'ww-demo-engineer', `sensor-po-${asOf}`);
    const poNumber = Number(created.entityId);
    if (!Number.isSafeInteger(poNumber) || poNumber <= 0) throw new Error('WhiskerWorks sensor supply order returned no valid PO number.');
    sensorOrder = { poNumber, status: 'Draft' };
  }
  if (sensorOrder.status === 'Draft') await post(`/api/procurement/purchase-orders/${sensorOrder.poNumber}/approve`, {}, 'ww-demo-certifier', `approve-sensor-po-${sensorOrder.poNumber}`);
  let bearingOrder = bearingPos[0];
  if (!bearingOrder) {
    const created = await post('/api/procurement/purchase-orders', { supplier: bearingSupply.supplierCode, lines: [{
      partNumber: bearingSupply.partNumber, quantity: bearingSupply.quantity, promisedDate: originalBearingPromise,
      unitPrice: bearingSupply.unitPrice, currency: bearingSupply.currency,
    }] }, 'ww-demo-engineer', `bearing-po-${asOf}`);
    const poNumber = Number(created.entityId);
    if (!Number.isSafeInteger(poNumber) || poNumber <= 0) throw new Error('WhiskerWorks bearing supply order returned no valid PO number.');
    bearingOrder = { poNumber, status: 'Draft' };
  }
  if (bearingOrder.status === 'Draft') await post(`/api/procurement/purchase-orders/${bearingOrder.poNumber}/approve`, {}, 'ww-demo-certifier', `approve-bearing-po-${bearingOrder.poNumber}`);

  const locationRows = new Map(existingLocations.map(item => [item.code, item]));
  for (const location of operationalLocations) {
    const current = locationRows.get(location.code);
    const expected = { siteCode: site.code, name: location.name, kind: location.kind, nettable: location.nettable, level: 'Zone' };
    if (current) checkSame('inventory location', location.code, expected, {
      siteCode: current.siteCode, name: current.name, kind: current.kind, nettable: current.nettable, level: current.level,
    });
    else await post('/api/inventory/locations', {
      code: location.code, siteCode: site.code, name: location.name, kind: location.kind, level: 'Zone', nettable: location.nettable,
      workCenterCode: location.workCenterCode,
    }, 'ww-demo-engineer');
  }

  const assetRows = new Map(existingAssets.map(asset => [asset.assetCode, asset]));
  for (const machine of machines) {
    const expected = machineAssetDefinition(machine);
    let current = assetRows.get(machine.code);
    if (!current) {
      await post('/api/maintenance/assets', expected, 'ww-demo-engineer');
      current = { ...expected, status: 'Planned' };
      assetRows.set(machine.code, current);
    }
    if (current.status !== 'Active') {
      await post(`/api/maintenance/assets/${encodeURIComponent(machine.code)}/status`, { status: 'Active', commissionedOn: '2026-01-01' }, 'ww-demo-engineer');
    }
  }

  // Fixed docks, racks, lanes, benches, cages and utilities are maintained as
  // non-machine assets. They deliberately have no MachineCode, so they cannot
  // create production capacity or satisfy machine-specific routing.
  for (const station of nonMachineStations) {
    const expected = stationAssetDefinition(station);
    let current = assetRows.get(station.code);
    if (!current) {
      await post('/api/maintenance/assets', expected, 'ww-demo-engineer');
      current = { ...expected, status: 'Planned' };
      assetRows.set(station.code, current);
    }
    if (current.status !== 'Active') {
      await post(`/api/maintenance/assets/${encodeURIComponent(station.code)}/status`, { status: 'Active', commissionedOn: '2026-01-01' }, 'ww-demo-engineer');
    }
  }

  const pmRows = new Map(existingPmPlans.map(plan => [plan.machineCode, plan]));
  for (const machine of machines) {
    const firstDue = addDays(asOf, 30);
    const current = pmRows.get(machine.code);
    if (current) {
      if (current.title !== `Fictional demo monthly PM · ${machine.code}` || current.intervalDays !== 30 || current.durationMinutes !== 60 || current.lockOutWhenOverdue !== true)
        throw new Error(`Existing WhiskerWorks PM plan for ${machine.code} differs from the versioned seed.`);
    } else {
      await post('/api/maintenance/plans', {
        machineCode: machine.code, title: `Fictional demo monthly PM · ${machine.code}`, intervalDays: 30, firstDue,
        durationMinutes: 60, tasks: ['Review guarding and interlocks', 'Record visual condition and lubrication check', 'Document simulated demo completion'],
        lockOutWhenOverdue: true, graceDays: 2,
      }, 'ww-demo-engineer');
    }
  }

  const gaugeDefinitions = [
    { code: 'GAUGE-TRQ-04', description: 'Fictional calibrated torque driver', calibrationDue: addDays(asOf, 90), certificate: 'WW-DEMO-CAL-TRQ-2026' },
    { code: 'GAUGE-OPT-01', description: 'Fictional optical power meter', calibrationDue: addDays(asOf, 90), certificate: 'WW-DEMO-CAL-OPT-2026' },
    { code: 'GAUGE-CAL-01', description: 'Fictional digital caliper', calibrationDue: addDays(asOf, 90), certificate: 'WW-DEMO-CAL-CAL-2026' },
  ];
  const gaugeRows = new Map(existingGauges.map(gauge => [gauge.code, gauge]));
  for (const gauge of gaugeDefinitions) {
    const current = gaugeRows.get(gauge.code);
    if (current) checkSame('quality gauge', gauge.code, gauge, {
      code: current.code, description: current.description, calibrationDue: current.calibrationDue, certificate: current.certificate,
    });
    else await post('/api/quality/gauges', gauge, 'ww-demo-engineer');
  }

  const planRows = new Map(existingPlans.map(plan => [`${plan.partNumber}:${plan.sequence}`, plan]));
  const testSequence = new Map([['WW-CAT-1000', 40], ['WW-CAT-2000', 50], ['WW-CAT-3000', 50], ['WW-CAT-4000', 50], ['WW-CAT-5000', 40]]);
  for (const product of products) {
    const sequence = testSequence.get(product.partNumber);
    const plan = {
      partNumber: product.partNumber, sequence, title: `Fictional final verification · ${product.partNumber}`, revision: 'A', plannedMinutes: 4,
      characteristics: [
        { name: 'Visual and assembly check', kind: 'Attribute', nominal: null, lower: null, upper: null, unit: null },
        { name: 'Fictional sample mass', kind: 'Variable', nominal: 1.5, lower: 0.1, upper: 25, unit: 'kg' },
      ],
    };
    const current = planRows.get(`${product.partNumber}:${sequence}`);
    if (current) {
      if (current.title !== plan.title || current.revision !== plan.revision || current.plannedMinutes !== plan.plannedMinutes || current.characteristics?.length !== plan.characteristics.length)
        throw new Error(`Existing inspection plan for ${product.partNumber} differs from the versioned seed.`);
    } else await post('/api/quality/plans', plan, 'ww-demo-engineer');
  }

  // A single receipt per purchased item gives the demo real, traceable lot rows and enough
  // stock to exercise MRP. Incoming inspection examples stay segregated from released stock.
  const purchased = allItems.filter(item => item.makeBuy === 'Buy');
  const positions = await get('/api/inventory/positions');
  const existingLots = new Set(positions.map(position => `${position.partNumber}:${position.lotNumber}`));
  let received = 0;
  for (const item of purchased) {
    const lotNumber = item.partNumber === 'WW-MOT-24V-DC' ? 'LOT-MOT-24V-881'
      : item.partNumber === 'WW-SISAL-SPOOL-250M' ? 'LOT-SISAL-M01'
        : `WW-${item.partNumber.slice(3)}-LOT-01`;
    if (existingLots.has(`${item.partNumber}:${lotNumber}`)) continue;
    const inspect = false;
    const quantity = item.partNumber === 'WW-SISAL-SPOOL-250M' ? 250
      : item.partNumber === 'WW-ROPE-KEV-500M' ? 500
        : item.partNumber === 'WW-YARN-BULK-1000M' ? 1000
          : item.unitOfMeasure === 'M' ? 1000 : 500;
    await post('/api/inventory/receipts', {
      partNumber: item.partNumber, quantity,
      locationCode: inspect ? 'WW-RECEIVING' : 'WW-RAW', lotNumber, supplierLot: `SUPLOT-${item.partNumber.slice(3)}`,
      inspect, identityKind: item.unitOfMeasure === 'M' ? 'Roll' : 'Lot', manufacturedOn: addDays(asOf, -7), expiresOn: null,
    }, 'ww-demo-engineer');
    received++;
  }

  let opened = 0;
  for (const item of allItems.filter(candidate => candidate.makeBuy === 'Make' && !products.some(product => product.partNumber === candidate.partNumber))) {
    const lotNumber = `WW-${item.partNumber.slice(3)}-OPEN-01`;
    if (existingLots.has(`${item.partNumber}:${lotNumber}`)) continue;
    const adjustment = await post('/api/inventory/adjustments', {
      partNumber: item.partNumber, locationCode: 'WW-RAW', lotNumber, status: 'Released', change: 500,
    }, 'ww-demo-engineer', `opening-${item.partNumber}`);
    const requestId = Number(adjustment.entityId);
    if (!Number.isSafeInteger(requestId) || requestId <= 0) throw new Error(`Opening-balance adjustment for ${item.partNumber} returned no request ID.`);
    await post(`/api/inventory/requests/${requestId}/approve`, {}, 'ww-demo-inventory');
    opened++;
  }

  const workload = createDemoWorkload(asOf);
  const demandByKey = new Set(existingDemand.map(item => item.sourceKey));
  for (const demand of workload.demandLines) {
    if (demandByKey.has(demand.sourceKey)) continue;
    await post('/api/planning/demand', { ...demand, programId, reason }, 'ww-demo-planner', demand.sourceKey);
  }
  const orderSignature = order => `${order.partNumber}|${order.dueDate}|${order.quantity}|${order.priority}`;
  const ordersBySignature = new Map();
  for (const order of existingOrders) {
    const signature = orderSignature(order);
    if (ordersBySignature.has(signature)) throw new Error(`Duplicate WW-DEMO production-order signature ${signature}; no orders were created or changed.`);
    ordersBySignature.set(signature, order);
  }
  for (const order of workload.productionOrders) {
    const signature = orderSignature(order);
    let existing = ordersBySignature.get(signature);
    let number;
    if (existing) {
      if (existing.programId !== programId) throw new Error(`Existing order ${existing.orderNumber} with the WhiskerWorks workload signature belongs outside WW-DEMO.`);
      number = existing.orderNumber;
    } else {
      // A stable idempotency key on the immutable create command makes a retry safe after a lost response.
      const created = await post('/api/orders/', { partNumber: order.partNumber, quantity: order.quantity, dueDate: order.dueDate, priority: order.priority, programId }, 'ww-demo-engineer', order.sourceKey);
      number = Number(created.entityId ?? created.orderNumber);
      if (!Number.isSafeInteger(number) || number <= 0) throw new Error(`Order create returned no valid order number for ${order.sourceKey}.`);
      existing = { orderNumber: number, partNumber: order.partNumber, quantity: order.quantity, dueDate: order.dueDate, priority: order.priority, programId, status: 'Planned' };
      ordersBySignature.set(signature, existing);
    }
    if (existing.status === 'Planned' && order.initialState !== 'Planned') {
      await post(`/api/orders/${number}/release`, {}, 'ww-demo-planner');
      existing.status = 'Released';
    }
    if (existing.status === 'Released' && order.initialState === 'Held') {
      await post(`/api/orders/${number}/hold`, {}, 'ww-demo-engineer');
      existing.status = 'Held';
    }
    // Orders already progressed beyond the seed state are left alone on reruns.
  }

  console.log(`Seeded WhiskerWorks commercial layer: ${allItems.length} items, ${knownBoms.length} released revisions/BOMs/routings, ${suppliers.length} suppliers, approved open sensor and bearing supply lines, ${operationalLocations.length} inventory locations, ${machines.length} production assets, ${nonMachineStations.length} fixed support assets, ${received} bought-part lots, ${opened} audited opening balances for made subassemblies, ${workload.demandLines.length} demand lines and ${workload.productionOrders.length} production orders.`);
  console.log(`Program boundary: WW-DEMO (${programId}); deterministic as-of: ${asOf}.`);
  console.log('All data is fictional and remains inside the designated isolated demo database. No reset/delete was performed.');
}

main().catch(error => { console.error(`WhiskerWorks commercial seed stopped: ${error.message}`); process.exitCode = 1; });
