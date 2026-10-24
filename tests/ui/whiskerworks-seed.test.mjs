import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import {
  baselinePresenceByCenter, demoPrincipals, machineActivities, machines, nonMachineStations, productFamilies, qualifications,
  scenarioQualificationExpiry, shifts, site, targets, workCenters, workers, zones,
} from '../../tools/whiskerworks-plant.mjs';

test('WhiskerWorks foundation matches its site, asset and roster targets', () => {
  assert.equal(site.code, 'WW-P01');
  assert.equal(zones.length, targets.zones);
  assert.equal(workCenters.length, targets.workCenters);
  assert.equal(machines.length, targets.machines);
  assert.equal(workers.length, targets.workers);
  assert.equal(productFamilies.length, targets.productFamilies);
  assert.equal(nonMachineStations.length, 14);
  assert.equal(shifts.length, 3);
  assert.equal(qualifications.length, 23, 'keep named mandatory qualification gates plus Toolpath / First-Article Verification');
  assert.equal(demoPrincipals.length, 7);
  // The seeding administrator's own program access is granted by a separate demo access manager.
  assert.deepEqual(demoPrincipals.find(principal => principal.subject === 'ww-demo-security')?.roles, ['SecurityAdministrator']);
  assert.deepEqual(scenarioQualificationExpiry, { machineCode: 'CNC-02', qualificationCode: 'QUAL-CNC-MILL', daysFromSeed: 5 });
  assert.equal(workers.filter(worker => worker.machineCode === scenarioQualificationExpiry.machineCode
    && worker.qualifications.includes(scenarioQualificationExpiry.qualificationCode)).length, 3,
  'the seed can select a deterministic CNC-02 operator for the individual S05 expiry without changing the qualification type policy');
  const attendanceImporter = demoPrincipals.find(principal => principal.subject === 'ww-demo-attendance-importer');
  assert.deepEqual(attendanceImporter?.roles, ['AttendanceImporter'], 'the HRIS import principal receives only its narrow import permission');
});

test('the sixteen work centers occupy unique cells in the 4x4 staffing board', () => {
  const slots = workCenters.map(center => `${center.layoutX}:${center.layoutY}`);
  assert.equal(new Set(slots).size, 16);
  assert.ok(workCenters.every(center => center.layoutX >= 0 && center.layoutX < 4 && center.layoutY >= 0 && center.layoutY < 4));
  const knownZones = new Set(zones.map(zone => zone.code));
  assert.ok(workCenters.every(center => knownZones.has(center.zoneCode)));
});

test('day-shift preview staffing is qualified for two-person and trainer-gated stations', () => {
  const dayWorkers = workers.filter(worker => worker.presentAtBaseline);
  assert.equal(dayWorkers.length, 32);
  assert.ok(workers.filter(worker => worker.shiftCode === 'SHIFT-SWG').length === 16);
  assert.ok(workers.filter(worker => worker.shiftCode === 'SHIFT-WKD').length === 12);
  assert.deepEqual(workers.slice(0, 52).map(worker => worker.code), Array.from({ length: 52 }, (_, index) => `WW-EMP-${String(index + 1).padStart(3, '0')}`), 'the original worker identities remain first and unchanged');
  assert.deepEqual(workers.slice(52).map(worker => worker.code), Array.from({ length: 8 }, (_, index) => `WW-EMP-${String(index + 53).padStart(3, '0')}`));
  assert.equal(workers.filter(worker => worker.shiftCode !== 'SHIFT-DAY' && !worker.presentAtBaseline).length, 28);
  for (const center of workCenters) {
    assert.equal(dayWorkers.filter(worker => worker.workCenterCode === center.code).length, 2, `${center.code} needs two preview workers`);
  }

  const weldTeam = dayWorkers.filter(worker => worker.workCenterCode === 'WC-FAB');
  assert.equal(weldTeam.length, 2);
  assert.ok(weldTeam.every(worker => worker.machineCode === 'FAB-WELD-01' && worker.qualifications.includes('QUAL-FAB-JOIN')));
  const latheTrainer = dayWorkers.find(worker => worker.workCenterCode === 'WC-CNC-LATHE' && worker.qualifications.includes('QUAL-TRAINER-CNC'));
  assert.ok(latheTrainer, 'a CNC lathe trainer is present in the day-shift preview');
});

test('machine assignments and qualification prerequisites resolve to defined manifest entries', () => {
  const machineByCode = new Map(machines.map(machine => [machine.code, machine]));
  const qualificationByCode = new Map(qualifications.map(qualification => [qualification.code, qualification]));
  for (const machine of machines) {
    assert.ok(workCenters.some(center => center.code === machine.workCenterCode), machine.code);
    for (const code of machine.operatorQualifications) assert.ok(qualificationByCode.has(code), `${machine.code}: ${code}`);
  }
  for (const profile of machineActivities) {
    assert.ok(machineByCode.has(profile.machineCode), `${profile.machineCode}:${profile.activityCode}`);
    assert.ok(profile.operatorQualifications.every(code => qualificationByCode.has(code)), `${profile.machineCode}:${profile.activityCode}`);
  }
  for (const worker of workers) {
    const machine = worker.machineCode ? machineByCode.get(worker.machineCode) : null;
    if (machine) {
      assert.equal(machine.workCenterCode, worker.workCenterCode, worker.subject);
      assert.equal(machine.activityCode, worker.activityCode, worker.subject);
      assert.ok(machine.operatorQualifications.every(code => worker.qualifications.includes(code)), worker.subject);
    }
    for (const code of worker.qualifications) assert.ok(qualificationByCode.has(code), `${worker.subject}: ${code}`);
  }
  for (const qualification of qualifications) {
    assert.ok(qualification.prerequisites.every(code => qualificationByCode.has(code)), qualification.code);
  }
  const setup = qualificationByCode.get('QUAL-CNC-SETUP');
  assert.ok(setup.prerequisites.includes('QUAL-CNC-TOOLPATH'));
  assert.deepEqual(setup.prerequisiteAlternatives.find(group => group.groupCode === 'MILL-OR-LATHE').qualificationCodes, ['QUAL-CNC-MILL', 'QUAL-CNC-LATHE']);
  assert.ok(machineActivities.some(profile => profile.machineCode === 'CNC-01' && profile.activityCode === 'CNC_SETUP' && profile.operatorQualifications.includes('QUAL-CNC-SETUP')));
  assert.ok(machineActivities.some(profile => profile.machineCode === 'CNC-02' && profile.activityCode === 'CNC_SETUP' && profile.operatorQualifications.includes('QUAL-CNC-SETUP')));
  assert.ok(workers.some(worker => worker.workCenterCode === 'WC-CNC-MILL' && worker.qualifications.includes('QUAL-CNC-SETUP')));
});

test('seed CLI defaults to a no-network dry run and advertises its isolated target', () => {
  const result = spawnSync(process.execPath, ['tools/seed-whiskerworks.mjs', '--base=http://127.0.0.1:1'], {
    encoding: 'utf8',
    timeout: 10_000,
  });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /dry run; no server or database calls made/);
  assert.match(result.stdout, /15 zones, 16 work centers, 24 machines, 60 fictional workers/);
  assert.match(result.stdout, /no reset\/delete operation/);
});

test('seed apply refuses implicit database and identity targets before contacting Forge', () => {
  const result = spawnSync(process.execPath, ['tools/seed-whiskerworks.mjs', '--apply', '--base=http://127.0.0.1:1', '--subject=dev-admin'], {
    encoding: 'utf8',
    timeout: 10_000,
  });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /explicit --base, --subject and --reader/);
});
