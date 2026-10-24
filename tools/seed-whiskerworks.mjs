#!/usr/bin/env node
// Seeds the WhiskerWorks resource/workforce foundation through Forge's audited APIs.
// This phase deliberately excludes product, order, stock and quality records; those need
// the later tenant-scoped commercial seed and scenario-control work.
import { createHash } from 'node:crypto';
import {
  activities, baselinePresenceByCenter, demoPrincipals, machineActivities, machines, nonMachineStations, qualifications, scenarioQualificationExpiry, shifts, site,
  targets, workCenterActivities, workCenters, workers, zones,
} from './whiskerworks-plant.mjs';

const args = process.argv.slice(2);
const value = name => args.find(argument => argument.startsWith(`--${name}=`))?.slice(name.length + 3);
const apply = args.includes('--apply');
const baseArg = value('base');
const subjectArg = value('subject');
const readerArg = value('reader');
const base = (baseArg ?? 'http://localhost:5080').replace(/\/+$/, '');
const adminSubject = subjectArg ?? 'dev-admin';
const readerSubject = readerArg ?? adminSubject;
const reason = 'Seed isolated WhiskerWorks fictional demo plant, phase 1';

function validateManifest() {
  const unique = (values, select, label) => {
    const keys = values.map(select);
    if (new Set(keys).size !== keys.length) throw new Error(`WhiskerWorks manifest has duplicate ${label}.`);
  };
  unique(zones, item => item.code, 'zone codes');
  unique(workCenters, item => item.code, 'work-center codes');
  unique(machines, item => item.code, 'machine codes');
  unique(machineActivities, item => `${item.machineCode}:${item.activityCode}`, 'machine activity profiles');
  unique(activities, item => item.code, 'activity codes');
  unique(qualifications, item => item.code, 'qualification codes');
  unique(workers, item => item.subject, 'employee subjects');
  unique(workers, item => item.displayName, 'employee display names');
  if (zones.length !== targets.zones || workCenters.length !== targets.workCenters || machines.length !== targets.machines || workers.length !== targets.workers) {
    throw new Error('WhiskerWorks manifest counts do not match their declared targets.');
  }
  const zoneCodes = new Set(zones.map(zone => zone.code));
  const centerCodes = new Set(workCenters.map(center => center.code));
  const machineCodes = new Set(machines.map(machine => machine.code));
  const activityCodes = new Set(activities.map(activity => activity.code));
  const qualificationCodes = new Set(qualifications.map(qualification => qualification.code));
  for (const qualification of qualifications) {
    if (qualification.prerequisites.some(code => !qualificationCodes.has(code))) throw new Error(`Unknown prerequisite for ${qualification.code}.`);
    for (const group of qualification.prerequisiteAlternatives ?? []) {
      if (!group.groupCode || !group.name || group.qualificationCodes.length < 2 || new Set(group.qualificationCodes).size !== group.qualificationCodes.length) {
        throw new Error(`Invalid alternative prerequisite group for ${qualification.code}.`);
      }
      if (group.qualificationCodes.some(code => !qualificationCodes.has(code))) throw new Error(`Unknown alternative prerequisite for ${qualification.code}.`);
      if (group.qualificationCodes.some(code => qualification.prerequisites.includes(code))) throw new Error(`Alternative prerequisite duplicates a mandatory prerequisite for ${qualification.code}.`);
    }
  }
  const visitedQualifications = new Set();
  const activeQualifications = new Set();
  const visitQualification = code => {
    if (activeQualifications.has(code)) throw new Error(`Qualification prerequisite cycle includes ${code}.`);
    if (visitedQualifications.has(code)) return;
    activeQualifications.add(code);
    const definition = qualifications.find(item => item.code === code);
    for (const prerequisite of definition.prerequisites) visitQualification(prerequisite);
    for (const group of definition.prerequisiteAlternatives ?? []) {
      for (const prerequisite of group.qualificationCodes) visitQualification(prerequisite);
    }
    activeQualifications.delete(code);
    visitedQualifications.add(code);
  };
  qualifications.forEach(item => visitQualification(item.code));
  for (const center of workCenters) if (!zoneCodes.has(center.zoneCode)) throw new Error(`Unknown zone ${center.zoneCode} for ${center.code}.`);
  const gridSlots = new Set();
  for (const center of workCenters) {
    if (!Number.isInteger(center.layoutX) || !Number.isInteger(center.layoutY) || center.layoutX < 0 || center.layoutX > 3 || center.layoutY < 0 || center.layoutY > 3) {
      throw new Error(`Work center ${center.code} is outside the 4x4 staffing layout.`);
    }
    const slot = `${center.layoutX}:${center.layoutY}`;
    if (gridSlots.has(slot)) throw new Error(`Work center layout slot ${slot} is assigned twice.`);
    gridSlots.add(slot);
  }
  for (const machine of machines) {
    if (!centerCodes.has(machine.workCenterCode) || !activityCodes.has(machine.activityCode)) throw new Error(`Invalid resource mapping for ${machine.code}.`);
  }
  for (const profile of machineActivities) {
    if (!machineCodes.has(profile.machineCode) || !activityCodes.has(profile.activityCode)) throw new Error(`Invalid machine activity mapping ${profile.machineCode}:${profile.activityCode}.`);
    if (profile.operatorQualifications.some(code => !qualificationCodes.has(code))) throw new Error(`Unknown qualification on ${profile.machineCode}:${profile.activityCode}.`);
  }
  for (const employee of workers) {
    if (!centerCodes.has(employee.workCenterCode) || !activityCodes.has(employee.activityCode)) throw new Error(`Invalid assignment plan for ${employee.subject}.`);
    if (employee.machineCode && !machineCodes.has(employee.machineCode)) throw new Error(`Unknown machine ${employee.machineCode} for ${employee.subject}.`);
    if (employee.qualifications.some(code => !qualificationCodes.has(code))) throw new Error(`Unknown qualification for ${employee.subject}.`);
    const centerWorkers = workers.filter(candidate => candidate.workCenterCode === employee.workCenterCode);
    const indexAtCenter = centerWorkers.findIndex(candidate => candidate.subject === employee.subject);
    const shouldBePresent = (baselinePresenceByCenter.get(employee.workCenterCode) ?? []).includes(indexAtCenter);
    if (employee.presentAtBaseline !== shouldBePresent || employee.presentAtBaseline !== (employee.shiftCode === 'SHIFT-DAY')) {
      throw new Error(`Baseline presence and shift disagree for ${employee.subject}.`);
    }
  }
  const shiftCounts = Object.fromEntries(shifts.map(shift => [shift.code, workers.filter(worker => worker.shiftCode === shift.code).length]));
  if (shiftCounts['SHIFT-DAY'] !== 32 || shiftCounts['SHIFT-SWG'] !== 16 || shiftCounts['SHIFT-WKD'] !== 12) {
    throw new Error(`WhiskerWorks shift distribution is invalid: ${JSON.stringify(shiftCounts)}.`);
  }
}

validateManifest();
if (!apply) {
  console.log('WhiskerWorks seed plan (dry run; no server or database calls made)');
  console.log(`Target: ${site.code} — ${site.name}`);
  console.log(`Foundation: ${zones.length} zones, ${workCenters.length} work centers, ${machines.length} machines, ${workers.length} fictional workers, ${qualifications.length} qualifications, ${shifts.length} shifts.`);
  console.log(`Additional fixed stations described: ${nonMachineStations.length}. Product families in the manifest: ${targets.productFamilies}; catalog/orders are a later phase.`);
  console.log('To apply to a separate empty Forge database, pass --apply --base=http://localhost:<port> --subject=<authorized-admin> --reader=<broad-read-user>.');
  console.log('This seed has no reset/delete operation and refuses databases containing another site or global demo-sensitive records.');
  process.exit(0);
}

if (!baseArg || !subjectArg || !readerArg) throw new Error('Applying requires explicit --base, --subject and --reader arguments; no default database target or implicit identity is allowed for writes.');
const targetUrl = new URL(base);
if (!['localhost', '127.0.0.1', '::1'].includes(targetUrl.hostname) && !args.includes('--allow-remote-demo')) {
  throw new Error('Refusing a non-local seed target. Pass --allow-remote-demo only for a deliberately isolated demo host.');
}

async function request(method, path, who = adminSubject, body = undefined) {
  const headers = { 'X-Forge-Dev-Subject': who, Accept: 'application/json' };
  if (body !== undefined) {
    body = { reason, ...body };
    headers['Content-Type'] = 'application/json';
    const hash = createHash('sha256').update(`${method}\n${path}\n${JSON.stringify(body)}`).digest('hex').slice(0, 32);
    headers['Idempotency-Key'] = `ww-foundation-v1-${hash}`;
  }
  const response = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} as ${who} returned ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const get = (path, who = adminSubject) => request('GET', path, who);
const post = (path, body, who = adminSubject) => request('POST', path, who, body);
const requireEmpty = (name, rows) => { if (rows.length) throw new Error(`Refusing to mix WhiskerWorks with existing ${name} (${rows.length} record(s)). Use a separate, empty Forge database.`); };
const onlyExpected = (name, rows, allowed, key = row => row.code) => {
  const foreign = rows.filter(row => !allowed.has(key(row)));
  if (foreign.length) throw new Error(`Refusing to mix WhiskerWorks with existing ${name}: ${foreign.slice(0, 5).map(key).join(', ')}. Use a separate, empty Forge database.`);
};
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);

async function preflight() {
  const [resources, people, workforce, items, orders, locations, qualificationTypes, suppliers, safetyRules, qualityPlans, assets, documents] = await Promise.all([
    get('/api/resources/?includeRetired=true'), get('/api/kernel/people'), get('/api/workforce/workers'),
    get('/api/engineering/items', readerSubject), get('/api/orders/', readerSubject), get('/api/inventory/locations', readerSubject),
    get('/api/competency/qualification-types', readerSubject), get('/api/procurement/suppliers', readerSubject), get('/api/safety-rules/', readerSubject),
    get('/api/quality/plans', readerSubject), get('/api/maintenance/assets', readerSubject), get('/api/engineering/documents', readerSubject),
  ]);

  onlyExpected('plant sites', resources.sites ?? [], new Set([site.code]));
  onlyExpected('safety areas', resources.safetyAreas ?? [], new Set(zones.map(zone => zone.code)));
  onlyExpected('work centers', resources.workCenters ?? [], new Set(workCenters.map(center => center.code)));
  onlyExpected('machines', resources.machines ?? [], new Set(machines.map(machine => machine.code)));
  onlyExpected('activity types', resources.activityTypes ?? [], new Set(activities.map(activity => activity.code)));
  onlyExpected('qualification types', qualificationTypes, new Set(qualifications.map(item => item.code)));
  onlyExpected('workers', workforce, new Set(workers.map(item => item.displayName)), item => item.displayName);
  onlyExpected('people', people, new Set([adminSubject, ...demoPrincipals.map(person => person.subject), ...workers.map(worker => worker.subject)]), item => item.idpSubject);
  onlyExpected('safety rules', safetyRules, new Set(['SAFE-FAB-2P', 'SAFE-ASM-2P']));
  requireEmpty('engineering items', items);
  requireEmpty('production orders', orders);
  requireEmpty('inventory locations', locations);
  requireEmpty('suppliers', suppliers);
  requireEmpty('quality plans', qualityPlans);
  requireEmpty('maintenance assets', assets);
  requireEmpty('controlled documents', documents);
  return { resources, people, workforce, qualificationTypes, safetyRules };
}

function ensureSame(label, code, expected, actual) {
  if (!same(expected, actual)) throw new Error(`Existing ${label} ${code} differs from the WhiskerWorks seed definition. No overwrite was attempted.`);
}

function validateExistingQualificationTypes(existingTypes) {
  const existingByCode = new Map(existingTypes.map(type => [type.code, type]));
  for (const definition of qualifications) {
    const existing = existingByCode.get(definition.code);
    if (!existing) continue;
    if (existing.name !== definition.name || existing.kind !== definition.kind)
      throw new Error(`Existing qualification type ${definition.code} differs from the WhiskerWorks seed definition. No records were changed.`);
    const expectedPrerequisites = new Set(definition.prerequisites);
    const currentPrerequisites = existing.prerequisites ?? [];
    if (currentPrerequisites.some(code => !expectedPrerequisites.has(code)))
      throw new Error(`Qualification ${definition.code} has an unexpected prerequisite. No records were changed.`);
    const expectedGroups = new Map((definition.prerequisiteAlternatives ?? []).map(group => [group.groupCode, group]));
    for (const group of existing.prerequisiteAlternatives ?? []) {
      const expected = expectedGroups.get(group.groupCode);
      if (!expected) throw new Error(`Qualification ${definition.code} has unexpected alternative prerequisite group ${group.groupCode}. No records were changed.`);
      ensureSame('alternative prerequisite group', `${definition.code}:${group.groupCode}`,
        { name: expected.name, qualificationCodes: [...expected.qualificationCodes].sort() },
        { name: group.name, qualificationCodes: [...(group.qualificationCodes ?? [])].sort() });
    }
  }
}

function validateExistingMachineActivities(resources) {
  const expected = new Map(machineActivities.map(profile => [`${profile.machineCode}:${profile.activityCode}`, profile]));
  for (const profile of resources.machineActivities ?? []) {
    const key = `${profile.machineCode}:${profile.activityCode}`;
    const planned = expected.get(key);
    if (!planned) throw new Error(`Unexpected machine activity profile ${key}; no seed writes were made.`);
    ensureSame('machine activity', key, [...planned.operatorQualifications].sort(), [...(profile.operatorQualifications ?? [])].sort());
  }
}

async function main() {
  await get('/health/ready').catch(error => { throw new Error(`Forge is not ready at ${base}: ${error.message}`); });
  const readerProfile = await get('/api/kernel/me', readerSubject);
  const requiredReadPermissions = ['engineering.read', 'execution.orders.read', 'inventory.read', 'procurement.read', 'quality.read', 'maintenance.read', 'competency.read', 'safety.rules.read'];
  const readerPermissions = new Set(readerProfile.permissions ?? []);
  const missingReaderPermissions = requiredReadPermissions.filter(permission => !readerPermissions.has(permission));
  if (missingReaderPermissions.length) throw new Error(`Preflight reader ${readerSubject} is missing required read permissions: ${missingReaderPermissions.join(', ')}.`);
  const state = await preflight();
  validateExistingQualificationTypes(state.qualificationTypes);
  validateExistingMachineActivities(state.resources);
  const resources = state.resources;
  const existingPeople = state.people;
  const workersByName = new Map(state.workforce.map(worker => [worker.displayName, worker]));
  const typeByCode = new Map(state.qualificationTypes.map(type => [type.code, type]));
  const centerByCode = new Map(workCenters.map(center => [center.code, center]));
  const zoneByCode = new Map(zones.map(zone => [zone.code, zone]));

  const peopleBySubject = new Map(existingPeople.map(person => [person.idpSubject, person]));
  async function ensurePerson(subject, displayName, roles) {
    let person = peopleBySubject.get(subject);
    if (!person) {
      const result = await post('/api/kernel/people', { idpSubject: subject, displayName }, adminSubject);
      person = { personId: result.entityId, idpSubject: subject, displayName, roles: [] };
      peopleBySubject.set(subject, person);
    } else if (person.displayName !== displayName) {
      throw new Error(`Existing demo identity ${subject} has a different name. No overwrite was attempted.`);
    }
    for (const role of roles) if (!(person.roles ?? []).includes(role)) {
      await post(`/api/kernel/people/${person.personId}/roles`, { role }, adminSubject);
      person.roles = [...(person.roles ?? []), role];
    }
    return person;
  }
  const engineer = await ensurePerson(demoPrincipals[0].subject, demoPrincipals[0].displayName, demoPrincipals[0].roles);
  const certifier = await ensurePerson(demoPrincipals[1].subject, demoPrincipals[1].displayName, demoPrincipals[1].roles);
  for (const principal of demoPrincipals.slice(2)) await ensurePerson(principal.subject, principal.displayName, principal.roles);

  const siteExisting = resources.sites.find(item => item.code === site.code);
  if (siteExisting) ensureSame('site', site.code, { name: site.name, timeZone: site.timeZone }, { name: siteExisting.name, timeZone: siteExisting.timeZone });
  else await post('/api/resources/sites', { code: site.code, name: site.name, timeZone: site.timeZone }, adminSubject);

  for (const zone of zones) {
    const existing = (resources.safetyAreas ?? []).find(item => item.code === zone.code);
    if (existing) ensureSame('safety area', zone.code, { siteCode: site.code, name: zone.name }, { siteCode: existing.siteCode, name: existing.name });
    else {
      await post('/api/resources/safety-areas', { code: zone.code, name: zone.name, parent: site.code }, 'ww-demo-engineer');
      resources.safetyAreas.push({ code: zone.code, siteCode: site.code, name: zone.name, active: true });
    }
  }

  const existingCenters = new Map((resources.workCenters ?? []).map(item => [item.code, item]));
  for (const center of workCenters) {
    const area = zoneByCode.get(center.zoneCode);
    const existing = existingCenters.get(center.code);
    if (existing) ensureSame('work center', center.code, { safetyAreaCode: center.zoneCode, name: center.name }, { safetyAreaCode: existing.safetyAreaCode, name: existing.name });
    else {
      await post('/api/resources/work-centers', { code: center.code, name: center.name, parent: area.code, picture: center.picture }, 'ww-demo-engineer');
      existingCenters.set(center.code, { code: center.code, safetyAreaCode: center.zoneCode, name: center.name, picture: center.picture, active: true });
    }
  }

  for (const activity of activities) {
    const existing = (resources.activityTypes ?? []).find(item => item.code === activity.code);
    if (existing) ensureSame('activity type', activity.code, { name: activity.name }, { name: existing.name });
    else {
      await post('/api/resources/activity-types', activity, 'ww-demo-engineer');
      resources.activityTypes.push({ ...activity, active: true });
    }
  }

  const existingMachines = new Map((resources.machines ?? []).map(item => [item.code, item]));
  for (const machine of machines) {
    const existing = existingMachines.get(machine.code);
    if (existing) ensureSame('machine', machine.code, { name: machine.name, workCenterCode: machine.workCenterCode }, { name: existing.name, workCenterCode: existing.workCenterCode });
    else {
      await post('/api/resources/machines', { code: machine.code, name: machine.name, parent: machine.workCenterCode }, 'ww-demo-engineer');
      existingMachines.set(machine.code, { code: machine.code, name: machine.name, workCenterCode: machine.workCenterCode, active: true });
    }
  }
  const centerActivities = new Set((resources.workCenterActivities ?? []).map(item => `${item.workCenterCode}:${item.activityCode}`));
  for (const item of workCenterActivities) {
    const key = `${item.workCenterCode}:${item.activityCode}`;
    if (!centerActivities.has(key)) await post(`/api/resources/work-centers/${encodeURIComponent(item.workCenterCode)}/activities`, { activityCode: item.activityCode }, 'ww-demo-engineer');
  }

  const mapPlacements = workCenters.map(center => ({ code: center.code, mapX: center.mapX, mapY: center.mapY }));
  const currentWithCoordinates = [...existingCenters.values()].filter(center => Number.isInteger(center.mapX) && Number.isInteger(center.mapY));
  for (const center of currentWithCoordinates) {
    const planned = mapPlacements.find(item => item.code === center.code);
    if (planned && (center.mapX !== planned.mapX || center.mapY !== planned.mapY)) throw new Error(`Work-center map position ${center.code} was changed after seeding. No overwrite was attempted.`);
  }
  if (currentWithCoordinates.length < workCenters.length) await post('/api/resources/work-centers/map-layout', { placements: mapPlacements }, 'ww-demo-engineer');
  const layoutPlacements = workCenters.map(center => ({ code: center.code, layoutX: center.layoutX, layoutY: center.layoutY }));
  await post('/api/resources/work-centers/layout', { placements: layoutPlacements }, 'ww-demo-engineer');

  const shiftsByCode = new Map((resources.shifts ?? []).map(item => [item.code, item]));
  for (const shift of shifts) {
    const existing = shiftsByCode.get(shift.code);
    if (existing) ensureSame('shift', shift.code, shift, { code: existing.code, siteCode: existing.siteCode, name: existing.name, start: existing.start, durationMinutes: existing.durationMinutes, days: existing.days });
    else {
      await post('/api/resources/shifts', shift, 'ww-demo-engineer');
      shiftsByCode.set(shift.code, shift);
    }
    const assignments = new Set((resources.workCenterShifts ?? []).map(item => `${item.workCenterCode}:${item.shiftCode}`));
    for (const center of workCenters) {
      const key = `${center.code}:${shift.code}`;
      if (!assignments.has(key)) await post(`/api/resources/work-centers/${encodeURIComponent(center.code)}/shifts`, { shiftCode: shift.code }, 'ww-demo-engineer');
    }
  }

  for (const definition of qualifications) {
    const existing = typeByCode.get(definition.code);
    if (existing) {
      if (existing.name !== definition.name || existing.kind !== definition.kind)
        throw new Error(`Existing qualification type ${definition.code} differs from the WhiskerWorks seed definition. No records were changed.`);
      const expectedPrerequisites = new Set(definition.prerequisites);
      if ((existing.prerequisites ?? []).some(code => !expectedPrerequisites.has(code)))
        throw new Error(`Qualification ${definition.code} has an unexpected prerequisite. No records were changed.`);
    }
    else {
      await post('/api/competency/qualification-types', { code: definition.code, name: definition.name, kind: definition.kind, validityDays: definition.validityDays }, 'ww-demo-engineer');
      typeByCode.set(definition.code, { ...definition });
    }
  }
  for (const definition of qualifications) {
    const existing = typeByCode.get(definition.code);
    for (const prerequisiteCode of definition.prerequisites) {
      if (!(existing.prerequisites ?? []).includes(prerequisiteCode)) {
        await post(`/api/competency/qualification-types/${encodeURIComponent(definition.code)}/prerequisites`, { prerequisiteCode }, 'ww-demo-engineer');
        existing.prerequisites = [...(existing.prerequisites ?? []), prerequisiteCode];
      }
    }
    const groupsByCode = new Map((existing.prerequisiteAlternatives ?? []).map(group => [group.groupCode, group]));
    const expectedGroups = new Map((definition.prerequisiteAlternatives ?? []).map(group => [group.groupCode, group]));
    for (const group of groupsByCode.values()) {
      const expected = expectedGroups.get(group.groupCode);
      if (!expected) throw new Error(`Qualification ${definition.code} has unexpected alternative prerequisite group ${group.groupCode}.`);
      ensureSame('alternative prerequisite group', `${definition.code}:${group.groupCode}`,
        { name: expected.name, qualificationCodes: [...expected.qualificationCodes].sort() },
        { name: group.name, qualificationCodes: [...(group.qualificationCodes ?? [])].sort() });
    }
    for (const group of expectedGroups.values()) {
      if (groupsByCode.has(group.groupCode)) continue;
      await post(`/api/competency/qualification-types/${encodeURIComponent(definition.code)}/prerequisite-alternatives`, {
        groupCode: group.groupCode, name: group.name, prerequisiteCodes: [...group.qualificationCodes].sort(),
      }, 'ww-demo-engineer');
      groupsByCode.set(group.groupCode, group);
    }
    existing.prerequisiteAlternatives = [...groupsByCode.values()];
  }

  // Machine activity profiles name operator qualifications, so they follow the qualification types.
  const machineProfiles = new Map((resources.machineActivities ?? []).map(item => [`${item.machineCode}:${item.activityCode}`, item]));
  // A machine whose manifest names no operator qualification (the mechanical cycle rig) gets no machine-specific profile;
  // Forge requires at least one qualification on a profile, and its work-center activity still applies.
  for (const profile of machineActivities.filter(item => item.operatorQualifications.length > 0)) {
    const key = `${profile.machineCode}:${profile.activityCode}`;
    const existing = machineProfiles.get(key);
    if (existing) ensureSame('machine activity', key, [...profile.operatorQualifications].sort(), [...(existing.operatorQualifications ?? [])].sort());
    else await post(`/api/resources/machines/${encodeURIComponent(profile.machineCode)}/activities`, { activityCode: profile.activityCode, operatorQualifications: profile.operatorQualifications }, 'ww-demo-engineer');
  }

  const employees = [];
  const scenarioWorker = workers.find(worker => worker.machineCode === scenarioQualificationExpiry.machineCode
    && worker.qualifications.includes(scenarioQualificationExpiry.qualificationCode));
  if (!scenarioWorker) throw new Error('The S05 expiring CNC qualification target is missing from the manifest.');
  const scenarioExpiresAt = new Date(Date.now() + scenarioQualificationExpiry.daysFromSeed * 24 * 60 * 60 * 1000).toISOString();
  const trainingByPerson = new Map();
  for (const worker of workers) {
    const person = await ensurePerson(worker.subject, worker.displayName, ['ShopFloorTerminal']);
    let current = workersByName.get(worker.displayName);
    if (current && current.personId !== person.personId) throw new Error(`Worker name collision for ${worker.displayName}; identity does not match ${worker.subject}.`);
    if (!current) {
      await post(`/api/workforce/workers/${person.personId}`, { homeWorkCenterCode: worker.workCenterCode }, 'ww-demo-engineer');
      current = { personId: person.personId, displayName: worker.displayName, homeWorkCenterCode: worker.workCenterCode, jobTitle: null, shiftCode: null, assignment: null, presenceKind: null };
      workersByName.set(worker.displayName, current);
    } else if (current.homeWorkCenterCode !== worker.workCenterCode) {
      throw new Error(`Existing seeded worker ${worker.displayName} has a different home work center. No overwrite was attempted.`);
    }
    if (current.jobTitle && current.jobTitle !== worker.jobTitle) throw new Error(`Existing seeded worker ${worker.displayName} has a different title. No overwrite was attempted.`);
    if (!current.jobTitle) await post(`/api/workforce/workers/${person.personId}/job-title`, { jobTitle: worker.jobTitle }, 'ww-demo-engineer');
    if (current.shiftCode && current.shiftCode !== worker.shiftCode) throw new Error(`Existing seeded worker ${worker.displayName} has a different shift. No overwrite was attempted.`);
    if (!current.shiftCode) await post(`/api/workforce/workers/${person.personId}/shift`, { shiftCode: worker.shiftCode }, 'ww-demo-engineer');

    const qualificationsForWorker = new Set();
    const visitPrerequisites = code => {
      if (qualificationsForWorker.has(code)) return;
      const type = typeByCode.get(code);
      if (!type) throw new Error(`Qualification ${code} is not defined.`);
      for (const prerequisite of type.prerequisites ?? []) visitPrerequisites(prerequisite);
      for (const group of type.prerequisiteAlternatives ?? []) {
        const requestedAlternative = group.qualificationCodes.find(candidate => worker.qualifications.includes(candidate));
        const selectedAlternative = requestedAlternative ?? [...group.qualificationCodes].sort()[0];
        visitPrerequisites(selectedAlternative);
      }
      qualificationsForWorker.add(code);
    };
    worker.qualifications.forEach(visitPrerequisites);
    let recorded = trainingByPerson.get(person.personId);
    if (!recorded) {
      const query = new URLSearchParams(); query.append('personIds', person.personId);
      recorded = await get(`/api/competency/people/qualifications?${query}`, 'ww-demo-engineer');
      trainingByPerson.set(person.personId, recorded);
    }
    for (const code of qualificationsForWorker) {
      const record = recorded.find(item => item.code === code);
      const scenarioExpiry = worker.code === scenarioWorker.code && code === scenarioQualificationExpiry.qualificationCode ? scenarioExpiresAt : null;
      if (record?.status === 'Qualified' && (!record.expiresAt || Date.parse(record.expiresAt) > Date.now())) {
        if (scenarioExpiry && (!record.expiresAt || Date.parse(record.expiresAt) > Date.parse(scenarioExpiry))) {
          throw new Error(`${worker.displayName} already has a longer ${code} validity than the S05 demo target. Review or recreate the isolated fictional demo rather than rewriting qualification history.`);
        }
        continue;
      }
      if (record && record.status !== 'InTraining') throw new Error(`${worker.displayName} has ${record.status} ${code}; review it instead of overwriting qualification history.`);
      if (!record) await post(`/api/competency/people/${person.personId}/training`, { code }, 'ww-demo-engineer');
      await post(`/api/competency/people/${person.personId}/certifications`, {
        code, evidence: 'Fictional WhiskerWorks demo baseline practical assessment; not a real-world credential.',
        ...(scenarioExpiry ? { expiresAt: scenarioExpiry } : {}),
      }, certifier.idpSubject);
      recorded.push({ personId: person.personId, code, status: 'Qualified', expiresAt: scenarioExpiry });
    }

    // Work on a machine without a machine-specific profile (no operator qualification) is assigned at the work center.
    const machineProfiled = !!worker.machineCode && machineActivities.some(item => item.machineCode === worker.machineCode
      && item.activityCode === worker.activityCode && item.operatorQualifications.length > 0);
    if (machineProfiled) {
      const expectedAssignment = { workCenterCode: worker.workCenterCode, machineCode: worker.machineCode, activityCode: worker.activityCode };
      const actualAssignment = current.assignment ? { workCenterCode: current.assignment.workCenterCode, machineCode: current.assignment.machineCode, activityCode: current.assignment.activityCode } : null;
      if (actualAssignment && !same(expectedAssignment, actualAssignment)) throw new Error(`Existing assignment for ${worker.displayName} differs from the seed plan. No overwrite was attempted.`);
      if (!actualAssignment) await post(`/api/workforce/workers/${person.personId}/assignment`, {
        safetyAreaCode: centerByCode.get(worker.workCenterCode).zoneCode, ...expectedAssignment, asTrainee: false,
      }, 'ww-demo-supervisor');
    } else if (!current.assignment) {
      await post(`/api/workforce/workers/${person.personId}/assignment`, {
        safetyAreaCode: centerByCode.get(worker.workCenterCode).zoneCode, workCenterCode: worker.workCenterCode,
        machineCode: null, activityCode: worker.activityCode, asTrainee: false,
      }, 'ww-demo-supervisor');
    }

    const presentAtBaseline = worker.presentAtBaseline;
    if (presentAtBaseline && current.presenceKind !== 'ClockedIn' && current.presenceKind !== 'SupervisorConfirmed') {
      await post(`/api/workforce/workers/${person.personId}/presence`, { kind: 'SupervisorConfirmed' }, 'ww-demo-supervisor');
    }
    employees.push(worker);
  }

  for (const rule of [
    { code: 'SAFE-FAB-2P', title: 'Two qualified people for heavy frame joining', scopeCode: 'FAB-WELD-01', activityCodes: ['FAB_JOIN'], qualification: 'QUAL-FAB-JOIN' },
    { code: 'SAFE-ASM-2P', title: 'Two qualified people for heavy tower assembly', scopeCode: 'ASM-03', activityCodes: ['ASSEMBLE_HEAVY'], qualification: 'QUAL-ASM-HVY' },
  ]) {
    const versions = state.safetyRules.filter(item => item.code === rule.code);
    const matchesDefinition = item => same({
      title: item.title,
      scopeKind: item.scopeKind,
      scopeCode: item.scopeCode,
      activityCodes: [...(item.activityCodes ?? [])].sort(),
      minimumPhysicalPeople: item.minimumPhysicalPeople,
      prohibitLoneWork: item.prohibitLoneWork,
      slots: (item.slots ?? []).map(slot => ({
        name: slot.name, count: slot.count, satisfiedBy: [...slot.satisfiedBy].sort(), traineeMayFill: slot.traineeMayFill,
      })).sort((left, right) => left.name.localeCompare(right.name)),
    }, {
      title: rule.title,
      scopeKind: 'Machine',
      scopeCode: rule.scopeCode,
      activityCodes: [...rule.activityCodes].sort(),
      minimumPhysicalPeople: 2,
      prohibitLoneWork: true,
      slots: [{ name: 'Qualified operator', count: 2, satisfiedBy: [rule.qualification], traineeMayFill: false }],
    });
    for (const existing of versions) {
      if (!matchesDefinition(existing)) throw new Error(`Existing safety rule ${rule.code} v${existing.version} differs from the WhiskerWorks definition. No overwrite was attempted.`);
    }
    if (versions.some(item => item.status === 'Published')) continue;
    let draft = versions.find(item => item.status === 'Draft');
    if (!draft) {
      await post('/api/safety-rules/drafts', {
        code: rule.code, title: rule.title, scopeKind: 'Machine', scopeCode: rule.scopeCode, activityCodes: rule.activityCodes,
        minimumPhysicalPeople: 2, prohibitLoneWork: true,
        slots: [{ name: 'Qualified operator', count: 2, satisfiedBy: [rule.qualification], traineeMayFill: false }],
      }, engineer.idpSubject);
      draft = { version: Math.max(0, ...versions.map(item => item.version)) + 1 };
    }
    await post(`/api/safety-rules/${encodeURIComponent(rule.code)}/versions/${draft.version}/publish`, {}, certifier.idpSubject);
  }

  console.log(`Seeded the WhiskerWorks foundation into ${site.code}: ${zones.length} zones, ${workCenters.length} work centers, ${machines.length} machines, ${employees.length} workers.`);
  console.log('This phase does not seed product revisions/BOMs, customers/suppliers, inventory, production orders, quality/maintenance records, badges or scenarios.');
}

main().catch(error => { console.error(`WhiskerWorks seed stopped: ${error.message}`); process.exitCode = 1; });
