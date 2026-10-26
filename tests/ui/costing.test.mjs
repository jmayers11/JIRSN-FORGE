import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('the NCR cost reconciliation shows each portion, the scrap loss and whether it adds up', async t => {
  installBrowser(t);
  const { reconciliationHtml, money } = await import('../../src/Forge.Host/wwwroot/js/pages/costing.js');
  assert.equal(money(160, 'USD'), '160.00 USD');
  assert.equal(money(null, 'USD'), '—');
  const html = reconciliationHtml({
    rootNcrId: 1, partNumber: 'ROLL-100', orderNumber: 7, method: 'Standard', currency: 'USD', valuedAt: '2026-10-25T12:00:00Z', unitStandardCost: 20,
    originalQuantity: 8, originalValue: 160, scrapLoss: 100, scrapQuantityRecorded: 5, quantitiesReconcile: true, valuesReconcile: true, notes: [],
    portions: [{ ncrId: 1, parentNcrId: null, quantity: 3, status: 'Dispositioned', disposition: 'Rework', treatment: 'Retained (rework)', value: 60 },
      { ncrId: 2, parentNcrId: 1, quantity: 5, status: 'Dispositioned', disposition: 'Scrap', treatment: 'Loss', value: 100 }],
    rework: [{ reworkId: 9, quantity: 3, laborHours: 0.4, laborAndOverhead: 30, material: 5.25, notes: [] }],
  });
  assert.match(html, /split from 1/);
  assert.match(html, /Values reconcile/);
  assert.match(html, /Scrap loss 100\.00 USD/);
  assert.match(html, /5\.25 USD/);
  assert.match(reconciliationHtml({ partNumber: 'P', method: 'Standard', currency: 'USD', valuedAt: null, unitStandardCost: null, originalQuantity: 1, originalValue: null,
    scrapLoss: null, scrapQuantityRecorded: 0, quantitiesReconcile: true, valuesReconcile: null, notes: ['No standard cost'], portions: [], rework: [] }), /Not valued/);
});
