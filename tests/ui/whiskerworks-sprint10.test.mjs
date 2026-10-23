import assert from 'node:assert/strict';
import test from 'node:test';
import { validateSprint10ScenarioCatalog, sprint10ScenarioCatalog } from '../../tools/whiskerworks-sprint10-scenarios.mjs';
import { targets, workers } from '../../tools/whiskerworks-plant.mjs';

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
