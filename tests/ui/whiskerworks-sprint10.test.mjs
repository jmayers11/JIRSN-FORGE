import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { validateSprint10ScenarioCatalog, sprint10ScenarioCatalog } from '../../tools/whiskerworks-sprint10-scenarios.mjs';
import { targets, workers } from '../../tools/whiskerworks-plant.mjs';
import { buildSprint10CompetencyPlan } from '../../tools/seed-whiskerworks-sprint10-competency.mjs';
import { buildSprint10AttendanceFact } from '../../tools/seed-whiskerworks-sprint10-attendance.mjs';

test('Sprint 10 has a complete 22-core plus 16-material acceptance matrix without claiming scenarios passed', () => {
  assert.equal(validateSprint10ScenarioCatalog(), true);
  assert.equal(sprint10ScenarioCatalog.length, 38);
  assert.equal(new Set(sprint10ScenarioCatalog.map(scenario => scenario.code)).size, 38);
  assert.ok(sprint10ScenarioCatalog.every(scenario => /Pending isolated-database scenario replay/.test(scenario.executionStatus)));
  assert.equal(sprint10ScenarioCatalog.filter(scenario => scenario.phase === 'core').length, 22);
  assert.equal(sprint10ScenarioCatalog.filter(scenario => scenario.phase === 'material-extension').length, 16);
});

test('Sprint 10 WhiskerWorks roster extends the original seed to 60 workers across all three shifts', () => {
  assert.equal(workers.length, targets.workers);
  assert.equal(targets.workers, 60);
  assert.deepEqual([...new Set(workers.map(worker => worker.shiftCode))].sort(), ['SHIFT-DAY', 'SHIFT-SWG', 'SHIFT-WKD']);
  assert.equal(workers.slice(0, 52).filter(worker => worker.presentAtBaseline).length, 32);
  assert.ok(workers.slice(52).every(worker => !worker.presentAtBaseline));
  assert.ok(workers.slice(52).some(worker => worker.qualifications.includes('QUAL-TRAINER-CNC')));
});

test('the acceptance catalog preserves the auditable attendance, recovery, and custody gates', () => {
  const byCode = new Map(sprint10ScenarioCatalog.map(scenario => [scenario.code, scenario]));
  assert.match(byCode.get('WW-S10-15').acceptance, /53\/55 eligible arrivals/);
  assert.match(byCode.get('WW-S10-02').acceptance, /source-center impact/);
  assert.match(byCode.get('WW-S10-24').acceptance, /genealogy/);
  assert.match(byCode.get('WW-S10-27').acceptance, /cannot reserve or issue more than 10/);
  assert.match(byCode.get('WW-S10-38').acceptance, /site and tenant boundaries/);
});

test('Sprint 10 competency seed plan links every machine assignment to a current roster qualification', () => {
  const plan = buildSprint10CompetencyPlan();
  assert.ok(plan.length >= 30);
  assert.equal(new Set(plan.map(item => `${item.workerSubject}:${item.machineCode}:${item.activityCode}:${item.qualificationCode}`)).size, plan.length);
  for (const item of plan) {
    const worker = workers.find(candidate => candidate.subject === item.workerSubject);
    assert.ok(worker, `Unknown fictional worker ${item.workerSubject}`);
    assert.ok(worker.qualifications.includes(item.qualificationCode));
    assert.equal(worker.machineCode, item.machineCode);
    assert.equal(worker.activityCode, item.activityCode);
  }
});

test('Sprint 10 attendance seed builds the exact reviewed 60-shift denominator fixture', () => {
  const focus = workers.find(worker => worker.shiftCode === 'SHIFT-DAY');
  const start = Date.UTC(2026, 0, 1, 6);
  const entries = Array.from({ length: 60 }, (_, index) => {
    const scheduledStart = start + index * 86_400_000;
    return { scheduledStart, fact: buildSprint10AttendanceFact(focus, index, scheduledStart, scheduledStart + 8 * 3_600_000, focus.subject) };
  });
  const facts = entries.map(entry => entry.fact);
  assert.equal(facts.filter(item => item.kind === 'ApprovedPto').length, 4);
  assert.equal(facts.filter(item => item.kind === 'ProtectedLeave').length, 1);
  const arrivals = entries.filter(item => item.fact.kind === 'Arrival');
  assert.equal(arrivals.length, 55);
  assert.equal(arrivals.filter(item => item.fact.observedAt <= item.scheduledStart + 5 * 60_000).length, 53);
  assert.equal(arrivals.filter(item => item.fact.observedAt > item.scheduledStart + 5 * 60_000).length, 2);
});

test('Sprint 10 history seeds are network-free by default and reject remote writes unless explicitly opted in', () => {
  const attendance = spawnSync(process.execPath, ['tools/seed-whiskerworks-sprint10-attendance.mjs', '--base=https://example.invalid'], { encoding: 'utf8', timeout: 10_000 });
  assert.equal(attendance.status, 0, attendance.stderr);
  assert.match(attendance.stdout, /dry run; no network or database calls/);
  assert.match(attendance.stdout, /53 on-time arrivals and 2 late arrivals/);
  const attendanceRemote = spawnSync(process.execPath, [
    'tools/seed-whiskerworks-sprint10-attendance.mjs', '--apply', '--base=https://example.invalid',
    '--admin=admin', '--reader=reader', '--importer=importer', '--reviewer=reviewer',
  ], { encoding: 'utf8', timeout: 10_000 });
  assert.equal(attendanceRemote.status, 1);
  assert.match(attendanceRemote.stderr, /Refusing a non-local seed target/);

  const competency = spawnSync(process.execPath, ['tools/seed-whiskerworks-sprint10-competency.mjs', '--base=https://example.invalid'], { encoding: 'utf8', timeout: 10_000 });
  assert.equal(competency.status, 0, competency.stderr);
  assert.match(competency.stdout, /practical assessments: 43/);
  const competencyRemote = spawnSync(process.execPath, [
    'tools/seed-whiskerworks-sprint10-competency.mjs', '--apply', '--base=https://example.invalid', '--subject=admin', '--reader=reader',
  ], { encoding: 'utf8', timeout: 10_000 });
  assert.equal(competencyRemote.status, 1);
  assert.match(competencyRemote.stderr, /Refusing a non-local seed target/);
});
