import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('an amendment reads as plain changes and only someone else may approve it', async t => {
  installBrowser(t);
  const { amendmentSummary, amendmentsHtml } = await import('../../src/Forge.Host/wwwroot/js/order-amendments.js');
  assert.equal(amendmentSummary({ newQuantity: 120, removeOperations: [30], addOperations: [{ sequence: 25, workCenterCode: 'ASSY-WC', description: 'Final pack' }],
    changeMaterials: [{ lineNumber: 1, requiredQuantity: 300 }] }), 'quantity → 120; remove op 30; add op 25 ASSY-WC Final pack; material line 1 → 300');
  assert.equal(amendmentSummary({}), 'no change');
  const list = [{ amendmentId: 4, status: 'Proposed', changes: { newPriority: 1 }, rationale: 'Expedite', proposedBy: 'person:eng', proposedAt: '2026-10-31T12:00:00Z' }];
  const planner = amendmentsHtml(list, { propose: false, approve: true }, 'person:planner');
  assert.match(planner, /data-amendment-approve="4"/);
  assert.doesNotMatch(planner, /data-propose-amendment/);
  const author = amendmentsHtml(list, { propose: true, approve: true }, 'person:eng');
  assert.doesNotMatch(author, /data-amendment-approve/);
  assert.match(author, /data-amendment-withdraw="4"/);
  assert.match(amendmentsHtml([], { propose: true }, null), /No amendments/);
});

test('a promise date answer names the date, the basis and the free stock', async t => {
  installBrowser(t);
  const { promiseText } = await import('../../src/Forge.Host/wwwroot/js/pages/sales.js');
  assert.match(promiseText({ quantity: 8, partNumber: 'ROLL-100', earliest: '2026-11-10', basis: 'Covered once Production order 10002 (Released) arrives.', freeNow: 4 }),
    /8 ROLL-100: earliest .*Covered once Production order 10002.*Free now: 4\./);
});
