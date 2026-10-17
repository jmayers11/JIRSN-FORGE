import assert from 'node:assert/strict';
import test from 'node:test';
import { inventoryPage } from '../../src/Forge.Host/wwwroot/js/pages/inventory.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

test('inventory lot selection shows ranked eligible and excluded candidates without reserving stock', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'inventory-selection-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/engineering/items') return Promise.resolve(response([{ partNumber: 'PART-A', description: 'Foil' }]));
    if (path === '/api/orders/') return Promise.resolve(response([{ orderNumber: 42, partNumber: 'PART-A', quantity: 10, status: 'Released' }]));
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [], workCenters: [], machines: [] }));
    if (path.startsWith('/api/inventory/selection?')) return Promise.resolve(response({ partNumber: 'PART-A', rule: 'Fefo', requiredRevision: 'B', candidates: [
      { lotNumber: 'LOT-1', locationCode: 'BIN-1', quantity: 6, received: '2026-10-01T00:00:00Z', expires: '2027-01-01', partRevision: 'B', rank: 1, excludedBecause: null },
      { lotNumber: 'LOT-2', locationCode: 'BIN-2', quantity: 4, received: '2026-09-01T00:00:00Z', expires: '2026-12-01', partRevision: 'A', rank: 0, excludedBecause: 'Revision does not match order.' },
    ] }));
    return Promise.resolve(response([]));
  };

  await inventoryPage(page, { me: { personId: 'viewer', permissions: [] }, query: new URLSearchParams('tab=selection'), params: [], refreshBell() {} });
  page.querySelector('#lot-selection-part').value = 'PART-A'; page.querySelector('#lot-selection-order').value = '42';
  fire(page.querySelector('#lot-selection-form'), 'submit'); await flush();

  const lookup = calls.find(path => path.startsWith('/api/inventory/selection?'));
  assert.ok(lookup); assert.match(lookup, /part=PART-A/); assert.match(lookup, /order=42/);
  const result = page.querySelector('#lot-selection-result');
  assert.match(result.innerHTML, /First expired, first out/); assert.match(result.innerHTML, /LOT-1/); assert.match(result.innerHTML, /Eligible candidate/);
  assert.match(result.innerHTML, /LOT-2/); assert.match(result.innerHTML, /Revision does not match order/);
  assert.match(result.innerHTML, /does not reserve or issue stock/);
});
