import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('the rework row shows what its plans add and offers each action only to the role that may take it', async t => {
  installBrowser(t);
  const { reworkPlanCell } = await import('../../src/Forge.Host/wwwroot/js/actuals.js');
  const rework = { reworkId: 7, quantity: 10, sequence: 20, closedAt: null };
  const proposed = {
    planId: 'a1', reworkId: 7, status: 'Proposed', rationale: 'Open joint',
    operations: [{ sequence: 30, description: 'Solder touch-up', inspection: false }, { sequence: 40, description: 'Retest', inspection: true }],
    materials: [{ partNumber: 'CONN-2', quantity: 10, consumedAtSequence: 30 }],
  };
  const engineer = { permissions: ['execution.orders.manage'] }, quality = { permissions: ['quality.approve'] };

  assert.match(reworkPlanCell(rework, [], engineer), /data-plan-rework="7"/);
  assert.equal(reworkPlanCell(rework, [], quality), '<span class="faint">None</span>');
  const pending = reworkPlanCell(rework, [proposed], quality);
  assert.match(pending, /Proposed/);
  assert.match(pending, /40 Retest \(inspection\)/);
  assert.match(pending, /10 CONN-2 at 30/);
  assert.match(pending, /data-decide-plan="a1" data-approve="true"/);
  assert.doesNotMatch(reworkPlanCell(rework, [proposed], engineer), /data-plan-rework|data-decide-plan/, 'one plan waits for a decision at a time; the engineer does not decide it');
  const decided = reworkPlanCell({ ...rework, closedAt: '2026-10-25T12:00:00Z' }, [{ ...proposed, status: 'Approved', decisionNote: 'MRB ok' }], engineer);
  assert.match(decided, /pill ok/);
  assert.match(decided, /MRB ok/);
  assert.doesNotMatch(decided, /data-plan-rework/);
});
